// WOGHook.dll - injected into Genesis.exe (War of Genesis Idle Loot, Unity 6 Il2Cpp + PuerTS).
//
// The idle battle runs in the game's TypeScript bundle (PuerTS on V8), not in C#. Everything
// that touches the game therefore runs on the main thread, inside a detour of
// NTSManager.ManagedUpdate (called every frame, right before the JS env ticks):
//   - Speed:     UnityEngine.Time.timeScale, capped at kMaxSpeed. Calling it from the worker
//                thread crashed UnityPlayer.dll, so it is only ever set here.
//   - Auto heal: a JS snippet pushed by the trainer is evaluated in the game's JS env every
//                few frames while heal is on.
//   - Commands:  the trainer can evaluate arbitrary JS (for research) or dump the decrypted
//                main bundle to a file.
// Communicates with the C# trainer through a named shared-memory block.

#include <windows.h>
#include <intrin.h>
#include <cstdint>
#include <cstdio>
#include <cstring>

static constexpr float kMaxSpeed  = 5.0f;
static constexpr int   kTextSize  = 1 << 20;   // command in/out text (UTF-8)
static constexpr int   kHealSize  = 16 << 10;  // heal script (UTF-8)
static constexpr int   kHealEvery = 6;         // frames between heal passes

// ---- Shared state: MUST match TrainerBridge.cs byte-for-byte (pack=1) ----
#pragma pack(push, 1)
struct SharedState
{
    int32_t magic;         // 0x00  'WOG2'
    int32_t heartbeat;     // 0x04  worker loop counter
    int32_t speedEnabled;  // 0x08
    float   timeScale;     // 0x0C
    int32_t healEnabled;   // 0x10
    int32_t status;        // 0x14  bit0=il2cpp, bit1=Time, bit2=frame hook, bit3=JS env seen
    int32_t trainerPaused; // 0x18  1 = trainer disconnected: stop touching the game
    int32_t frameCount;    // 0x1C  main-thread frames seen by the detour
    int32_t hookError;     // 0x20  why the frame detour failed (0 = none)
    int32_t healVersion;   // 0x24  bump after writing healScript
    int32_t healError;     // 0x28  1 = last heal pass threw (text in healMessage)
    int32_t cmdRequest;    // 0x2C  bump to run cmdType with text
    int32_t cmdDone;       // 0x30  == cmdRequest when finished
    int32_t cmdType;       // 0x34  1 = eval JS, 2 = dump main bundle to path in text
    int32_t cmdStatus;     // 0x38  1 = ok, <0 = error
    int32_t cmdLength;     // 0x3C  bytes of result in text
    char    healMessage[256];   // 0x40
    char    healScript[kHealSize];
    char    text[kTextSize];
};
#pragma pack(pop)

static const wchar_t* kMapName = L"WOGTrainerShared2";
static const int32_t  kMagic   = 0x32474F57;   // 'WOG2'

// ---- il2cpp exports ----
typedef void*  (*il2cpp_domain_get_t)();
typedef void*  (*il2cpp_thread_attach_t)(void*);
typedef void   (*il2cpp_thread_detach_t)(void*);
typedef void** (*il2cpp_domain_get_assemblies_t)(void*, size_t*);
typedef void*  (*il2cpp_assembly_get_image_t)(void*);
typedef void*  (*il2cpp_class_from_name_t)(void*, const char*, const char*);
typedef void*  (*il2cpp_class_get_method_from_name_t)(void*, const char*, int);
typedef void*  (*il2cpp_class_get_field_from_name_t)(void*, const char*);
typedef void   (*il2cpp_field_static_get_value_t)(void*, void*);
typedef void*  (*il2cpp_runtime_invoke_t)(void*, void*, void**, void**);

static il2cpp_domain_get_t                 il2cpp_domain_get;
static il2cpp_thread_attach_t              il2cpp_thread_attach;
static il2cpp_thread_detach_t              il2cpp_thread_detach;
static il2cpp_domain_get_assemblies_t      il2cpp_domain_get_assemblies;
static il2cpp_assembly_get_image_t         il2cpp_assembly_get_image;
static il2cpp_class_from_name_t            il2cpp_class_from_name;
static il2cpp_class_get_method_from_name_t il2cpp_class_get_method_from_name;
static il2cpp_class_get_field_from_name_t  il2cpp_class_get_field_from_name;
static il2cpp_field_static_get_value_t     il2cpp_field_static_get_value;
static il2cpp_runtime_invoke_t             il2cpp_runtime_invoke;

// ---- puerts.dll exports. Every call takes the JsEnv.isolate handle first. ----
typedef void*       (*PuertsEval_t)(void* isolate, const char* code, const char* path);
typedef const char* (*PuertsLastException_t)(void* isolate, int* length);
static PuertsEval_t          PuertsEval;
static PuertsLastException_t PuertsLastException;

static SharedState*  g_shared = nullptr;
static volatile bool g_shuttingDown = false;
static void*         g_setTimeScale = nullptr;
static void*         g_ntsEnvField = nullptr;     // static NTSManager.<NTsEnv>k__BackingField
static void*         g_loadMainScript = nullptr;  // NTSEnv.LoadMainScript()

typedef void (*ManagedUpdateFn)(void* self, const void* method);
static ManagedUpdateFn g_origManagedUpdate = nullptr;

// Field offsets (dump of this build)
static constexpr size_t kJsEnvIsolate = 0x50;     // Puerts.JsEnv.isolate (IntPtr)
static constexpr size_t kJsEnvDisposed = 0xA8;    // Puerts.JsEnv.disposed (bool)

template <typename T>
static T Resolve(HMODULE mod, const char* name)
{
    return reinterpret_cast<T>(GetProcAddress(mod, name));
}

static bool LoadIl2Cpp()
{
    HMODULE ga = GetModuleHandleW(L"GameAssembly.dll");
    if (!ga) return false;
    il2cpp_domain_get                 = Resolve<il2cpp_domain_get_t>(ga, "il2cpp_domain_get");
    il2cpp_thread_attach              = Resolve<il2cpp_thread_attach_t>(ga, "il2cpp_thread_attach");
    il2cpp_thread_detach              = Resolve<il2cpp_thread_detach_t>(ga, "il2cpp_thread_detach");
    il2cpp_domain_get_assemblies      = Resolve<il2cpp_domain_get_assemblies_t>(ga, "il2cpp_domain_get_assemblies");
    il2cpp_assembly_get_image         = Resolve<il2cpp_assembly_get_image_t>(ga, "il2cpp_assembly_get_image");
    il2cpp_class_from_name            = Resolve<il2cpp_class_from_name_t>(ga, "il2cpp_class_from_name");
    il2cpp_class_get_method_from_name = Resolve<il2cpp_class_get_method_from_name_t>(ga, "il2cpp_class_get_method_from_name");
    il2cpp_class_get_field_from_name  = Resolve<il2cpp_class_get_field_from_name_t>(ga, "il2cpp_class_get_field_from_name");
    il2cpp_field_static_get_value     = Resolve<il2cpp_field_static_get_value_t>(ga, "il2cpp_field_static_get_value");
    il2cpp_runtime_invoke             = Resolve<il2cpp_runtime_invoke_t>(ga, "il2cpp_runtime_invoke");
    return il2cpp_domain_get && il2cpp_thread_attach && il2cpp_thread_detach && il2cpp_domain_get_assemblies &&
           il2cpp_assembly_get_image && il2cpp_class_from_name &&
           il2cpp_class_get_method_from_name && il2cpp_class_get_field_from_name &&
           il2cpp_field_static_get_value && il2cpp_runtime_invoke;
}

static bool LoadPuerts()
{
    HMODULE pu = GetModuleHandleW(L"puerts.dll");
    if (!pu) return false;
    PuertsEval          = Resolve<PuertsEval_t>(pu, "Eval");
    PuertsLastException = Resolve<PuertsLastException_t>(pu, "GetLastExceptionInfo");
    return PuertsEval && PuertsLastException;
}

static void* FindClass(void* domain, const char* ns, const char* name)
{
    size_t count = 0;
    void** assemblies = il2cpp_domain_get_assemblies(domain, &count);
    for (size_t i = 0; i < count; ++i)
    {
        void* img = il2cpp_assembly_get_image(assemblies[i]);
        if (!img) continue;
        void* klass = il2cpp_class_from_name(img, ns, name);
        if (klass) return klass;
    }
    return nullptr;
}

// ---------------------------------------------------------------------------------------
// Main-thread work
// ---------------------------------------------------------------------------------------

static void SetTimeScale(float v)
{
    if (!g_setTimeScale) return;
    void* args[1] = { &v };
    void* exc = nullptr;
    il2cpp_runtime_invoke(g_setTimeScale, nullptr, args, &exc);
}

static void* CurrentJsEnv()
{
    if (!g_ntsEnvField) return nullptr;
    void* env = nullptr;
    il2cpp_field_static_get_value(g_ntsEnvField, &env);
    if (!env || *(reinterpret_cast<uint8_t*>(env) + kJsEnvDisposed)) return nullptr;
    return env;
}

static void* CurrentIsolate()
{
    void* env = CurrentJsEnv();
    return env ? *reinterpret_cast<void**>(reinterpret_cast<uint8_t*>(env) + kJsEnvIsolate) : nullptr;
}

static size_t CopyOut(char* dst, size_t cap, const char* src, size_t len)
{
    if (len >= cap) len = cap - 1;
    memcpy(dst, src, len);
    dst[len] = '\0';
    return len;
}

// Runs `code` in the game's JS env. The wrapper always throws, so the value (or the real
// error) comes back through GetLastExceptionInfo: "R:" + JSON/text on success, "E:" + error.
static bool EvalJs(void* isolate, const char* code, char* out, size_t cap, size_t* outLen)
{
    static char wrapped[kTextSize + 512];
    int n = _snprintf_s(wrapped, sizeof(wrapped), _TRUNCATE,
        "(function(){let __r;try{__r=(0,eval)(%s);}catch(e){throw 'E:'+(e&&e.stack||e);}"
        "if(typeof __r!=='string'){try{__r=JSON.stringify(__r);}catch(e){__r=String(__r);}}"
        "throw 'R:'+__r;})()",
        code);
    if (n < 0) { *outLen = CopyOut(out, cap, "E:script too long", 17); return false; }

    PuertsEval(isolate, wrapped, "wog://eval");
    int len = 0;
    const char* msg = PuertsLastException(isolate, &len);
    if (!msg || len <= 0) { *outLen = CopyOut(out, cap, "E:no result", 11); return false; }
    // Exception text may be prefixed ("Uncaught ..."); find our marker.
    const char* p = msg;
    const char* end = msg + len;
    for (const char* q = msg; q + 1 < end; ++q)
        if ((q[0] == 'R' || q[0] == 'E') && q[1] == ':') { p = q; break; }
    *outLen = CopyOut(out, cap, p, static_cast<size_t>(end - p));
    return p[0] == 'R';
}

// JS source literal for EvalJs: the trainer sends plain code; we pass it to indirect eval as
// a JSON-ish string literal so newlines/quotes survive.
static size_t QuoteJs(const char* src, char* dst, size_t cap)
{
    size_t o = 0;
    auto put = [&](char c) { if (o + 1 < cap) dst[o++] = c; };
    put('"');
    for (const char* s = src; *s; ++s)
    {
        char c = *s;
        if (c == '"' || c == '\\') { put('\\'); put(c); }
        else if (c == '\n') { put('\\'); put('n'); }
        else if (c == '\r') { put('\\'); put('r'); }
        else if (c == '\t') { put('\\'); put('t'); }
        else put(c);
    }
    put('"');
    dst[o] = '\0';
    return o;
}

static void RunEvalCommand(void* isolate)
{
    static char quoted[kTextSize + 64];
    QuoteJs(g_shared->text, quoted, sizeof(quoted));
    size_t len = 0;
    bool ok = EvalJs(isolate, quoted, g_shared->text, kTextSize, &len);
    g_shared->cmdLength = static_cast<int32_t>(len);
    g_shared->cmdStatus = ok ? 1 : -1;
}

static void RunDumpMainScript()
{
    void* env = CurrentJsEnv();
    if (!env || !g_loadMainScript) { g_shared->cmdStatus = -2; return; }
    void* exc = nullptr;
    void* str = il2cpp_runtime_invoke(g_loadMainScript, env, nullptr, &exc);
    if (exc || !str) { g_shared->cmdStatus = -3; return; }
    // Il2CppString: int32 length @0x10, UTF-16 chars @0x14
    int32_t n = *reinterpret_cast<int32_t*>(static_cast<uint8_t*>(str) + 0x10);
    const wchar_t* chars = reinterpret_cast<const wchar_t*>(static_cast<uint8_t*>(str) + 0x14);

    wchar_t path[MAX_PATH] = {};
    MultiByteToWideChar(CP_UTF8, 0, g_shared->text, -1, path, MAX_PATH);
    HANDLE f = CreateFileW(path, GENERIC_WRITE, 0, nullptr, CREATE_ALWAYS, 0, nullptr);
    if (f == INVALID_HANDLE_VALUE) { g_shared->cmdStatus = -4; return; }
    int bytes = WideCharToMultiByte(CP_UTF8, 0, chars, n, nullptr, 0, nullptr, nullptr);
    char* buf = static_cast<char*>(VirtualAlloc(nullptr, bytes + 1, MEM_COMMIT, PAGE_READWRITE));
    if (buf)
    {
        WideCharToMultiByte(CP_UTF8, 0, chars, n, buf, bytes, nullptr, nullptr);
        DWORD w = 0;
        WriteFile(f, buf, bytes, &w, nullptr);
        VirtualFree(buf, 0, MEM_RELEASE);
    }
    CloseHandle(f);
    g_shared->cmdLength = n;
    g_shared->cmdStatus = buf ? 1 : -5;
}

static int32_t g_healVersionLoaded = -1;

static void RunHeal(void* isolate)
{
    static char out[512];
    static char quoted[kHealSize * 2 + 64];
    size_t len = 0;
    if (g_healVersionLoaded != g_shared->healVersion)
    {
        // Install the trainer's script; it must define globalThis.__wogHeal.
        g_healVersionLoaded = g_shared->healVersion;
        QuoteJs(g_shared->healScript, quoted, sizeof(quoted));
        if (!EvalJs(isolate, quoted, out, sizeof(out), &len))
        {
            g_shared->healError = 1;
            CopyOut(g_shared->healMessage, sizeof(g_shared->healMessage), out, len);
            return;
        }
    }
    bool ok = EvalJs(isolate, "\"typeof __wogHeal==='function'?__wogHeal():'no __wogHeal'\"",
                     out, sizeof(out), &len);
    g_shared->healError = ok ? 0 : 1;
    CopyOut(g_shared->healMessage, sizeof(g_shared->healMessage), out, len);
}

static bool g_speedApplied = false;

static void OnMainThreadFrame()
{
    SharedState* s = g_shared;
    if (!s || g_shuttingDown) return;
    s->frameCount++;

    if (s->trainerPaused)
    {
        if (g_speedApplied) { SetTimeScale(1.0f); g_speedApplied = false; }
        return;
    }

    if (s->speedEnabled)
    {
        float ts = s->timeScale;
        if (!(ts >= 0.1f)) ts = 1.0f;
        if (ts > kMaxSpeed) ts = kMaxSpeed;
        SetTimeScale(ts);
        g_speedApplied = true;
    }
    else if (g_speedApplied)
    {
        SetTimeScale(1.0f);
        g_speedApplied = false;
    }

    void* isolate = (PuertsEval || LoadPuerts()) ? CurrentIsolate() : nullptr;
    if (isolate) s->status |= 8;

    if (s->cmdRequest != s->cmdDone)
    {
        int32_t req = s->cmdRequest;
        s->cmdStatus = 0;
        s->cmdLength = 0;
        if (s->cmdType == 2) RunDumpMainScript();
        else if (isolate) RunEvalCommand(isolate);
        else s->cmdStatus = -2;
        s->cmdDone = req;
    }

    if (s->healEnabled && isolate && s->healScript[0] && (s->frameCount % kHealEvery) == 0)
        RunHeal(isolate);
}

static void HookedManagedUpdate(void* self, const void* method)
{
    OnMainThreadFrame();
    g_origManagedUpdate(self, method);
}

// ---------------------------------------------------------------------------------------
// Detour of NTSManager.ManagedUpdate. Its first 16 bytes in this build:
//   40 53                 push rbx
//   48 83 EC 20           sub rsp, 20h
//   80 3D xx xx xx xx 00  cmp byte ptr [rip+disp], 0      (RIP-relative: rebuilt below)
//   48 8B D9              mov rbx, rcx
// ---------------------------------------------------------------------------------------

static void WriteAbsJmp(uint8_t* at, const void* to)
{
    at[0] = 0xFF; at[1] = 0x25;                       // jmp [rip+0]
    memset(at + 2, 0, 4);
    memcpy(at + 6, &to, 8);
}

static int32_t InstallManagedUpdateHook(void* target)
{
    auto* t = static_cast<uint8_t*>(target);
    static const uint8_t head[8] = { 0x40, 0x53, 0x48, 0x83, 0xEC, 0x20, 0x80, 0x3D };
    static const uint8_t tail[4] = { 0x00, 0x48, 0x8B, 0xD9 };
    if (memcmp(t, head, 8) != 0 || memcmp(t + 12, tail, 4) != 0) return 2;   // game updated
    if (reinterpret_cast<uintptr_t>(t) & 15) return 3;

    int32_t disp;
    memcpy(&disp, t + 8, 4);
    uint8_t* flag = t + 13 + disp;

    auto* tr = static_cast<uint8_t*>(VirtualAlloc(nullptr, 64, MEM_COMMIT | MEM_RESERVE,
                                                  PAGE_EXECUTE_READWRITE));
    if (!tr) return 4;
    size_t o = 0;
    memcpy(tr + o, t, 6); o += 6;                                 // push rbx; sub rsp,20h
    tr[o++] = 0x48; tr[o++] = 0xB8; memcpy(tr + o, &flag, 8); o += 8;   // mov rax, flag
    tr[o++] = 0x80; tr[o++] = 0x38; tr[o++] = 0x00;               // cmp byte ptr [rax], 0
    tr[o++] = 0x48; tr[o++] = 0x8B; tr[o++] = 0xD9;               // mov rbx, rcx
    WriteAbsJmp(tr + o, t + 16);                                  // back to the jne
    FlushInstructionCache(GetCurrentProcess(), tr, 64);
    g_origManagedUpdate = reinterpret_cast<ManagedUpdateFn>(tr);

    // 14-byte jmp + 2 nops, written with one 16-byte atomic store.
    alignas(16) uint8_t patch[16];
    WriteAbsJmp(patch, reinterpret_cast<void*>(&HookedManagedUpdate));
    patch[14] = 0x90; patch[15] = 0x90;

    DWORD old;
    if (!VirtualProtect(t, 16, PAGE_EXECUTE_READWRITE, &old)) return 5;
    alignas(16) int64_t expected[2];
    memcpy(expected, t, 16);
    int64_t desired[2];
    memcpy(desired, patch, 16);
    _InterlockedCompareExchange128(reinterpret_cast<volatile int64_t*>(t),
                                   desired[1], desired[0], expected);
    VirtualProtect(t, 16, old, &old);
    FlushInstructionCache(GetCurrentProcess(), t, 16);
    return memcmp(t, patch, 16) == 0 ? 0 : 6;
}

// ---------------------------------------------------------------------------------------
// Shutdown watch: stop doing work once the game window goes away.
// ---------------------------------------------------------------------------------------

static HWND g_unityHwnd = nullptr;

static LRESULT CALLBACK ShutdownCallWndHook(int code, WPARAM wParam, LPARAM lParam)
{
    if (code == HC_ACTION)
    {
        auto* m = reinterpret_cast<CWPSTRUCT*>(lParam);
        if ((m->message == WM_DESTROY && g_unityHwnd && m->hwnd == g_unityHwnd) ||
            (m->message == WM_ENDSESSION && m->wParam))
            g_shuttingDown = true;
    }
    return CallNextHookEx(nullptr, code, wParam, lParam);
}

static LRESULT CALLBACK ShutdownGetMsgHook(int code, WPARAM wParam, LPARAM lParam)
{
    if (code == HC_ACTION && reinterpret_cast<MSG*>(lParam)->message == WM_QUIT)
        g_shuttingDown = true;
    return CallNextHookEx(nullptr, code, wParam, lParam);
}

static BOOL CALLBACK FindUnityWindow(HWND hwnd, LPARAM out)
{
    DWORD pid = 0;
    DWORD tid = GetWindowThreadProcessId(hwnd, &pid);
    char cls[64] = {};
    GetClassNameA(hwnd, cls, sizeof(cls));
    if (pid == GetCurrentProcessId() && strcmp(cls, "UnityWndClass") == 0)
    {
        g_unityHwnd = hwnd;
        *reinterpret_cast<DWORD*>(out) = tid;
        return FALSE;
    }
    return TRUE;
}

static void InstallShutdownWatch()
{
    DWORD mainThread = 0;
    EnumWindows(FindUnityWindow, reinterpret_cast<LPARAM>(&mainThread));
    if (!mainThread) return;
    SetWindowsHookExW(WH_CALLWNDPROC, ShutdownCallWndHook, nullptr, mainThread);
    SetWindowsHookExW(WH_GETMESSAGE, ShutdownGetMsgHook, nullptr, mainThread);
}

// ---------------------------------------------------------------------------------------

static DWORD WINAPI WorkerThread(LPVOID)
{
    for (int tries = 0; tries < 2400 && !LoadIl2Cpp(); ++tries)
        Sleep(50);
    if (!il2cpp_domain_get) return 0;

    void* domain = nullptr;
    for (int tries = 0; tries < 600 && !domain; ++tries)
    {
        domain = il2cpp_domain_get();
        if (!domain) Sleep(50);
    }
    if (!domain) return 0;
    // Only for the class/method lookups below; the worker never calls game code, and an
    // attached thread would keep il2cpp waiting at shutdown.
    void* il2cppThread = il2cpp_thread_attach(domain);

    HANDLE hMap = CreateFileMappingW(INVALID_HANDLE_VALUE, nullptr, PAGE_READWRITE,
                                     0, sizeof(SharedState), kMapName);
    if (!hMap) return 0;
    if (GetLastError() == ERROR_ALREADY_EXISTS)
    {
        // Another copy of the hook (injected from a different folder) already owns the game.
        CloseHandle(hMap);
        il2cpp_thread_detach(il2cppThread);
        return 0;
    }
    g_shared = static_cast<SharedState*>(MapViewOfFile(hMap, FILE_MAP_ALL_ACCESS, 0, 0,
                                                       sizeof(SharedState)));
    if (!g_shared) return 0;

    int32_t status = 1;
    if (void* timeClass = FindClass(domain, "UnityEngine", "Time"))
        g_setTimeScale = il2cpp_class_get_method_from_name(timeClass, "set_timeScale", 1);
    if (g_setTimeScale) status |= 2;
    if (void* ntsEnv = FindClass(domain, "", "NTSEnv"))
        g_loadMainScript = il2cpp_class_get_method_from_name(ntsEnv, "LoadMainScript", 0);
    LoadPuerts();

    int32_t hookErr = 1;
    if (void* mgr = FindClass(domain, "", "NTSManager"))
    {
        g_ntsEnvField = il2cpp_class_get_field_from_name(mgr, "<NTsEnv>k__BackingField");
        void* m = il2cpp_class_get_method_from_name(mgr, "ManagedUpdate", 0);
        if (m && g_ntsEnvField) hookErr = InstallManagedUpdateHook(*reinterpret_cast<void**>(m));
    }
    if (hookErr == 0) status |= 4;
    g_shared->hookError = hookErr;
    g_shared->status    = status;
    g_shared->magic     = kMagic;   // last: the trainer waits for this

    il2cpp_thread_detach(il2cppThread);

    InstallShutdownWatch();
    for (;;)
    {
        g_shared->heartbeat++;
        Sleep(100);
    }
}

BOOL APIENTRY DllMain(HMODULE hModule, DWORD reason, LPVOID)
{
    if (reason == DLL_PROCESS_ATTACH)
    {
        DisableThreadLibraryCalls(hModule);
        CreateThread(nullptr, 0, WorkerThread, nullptr, 0, nullptr);
    }
    else if (reason == DLL_PROCESS_DETACH)
    {
        g_shuttingDown = true;
    }
    return TRUE;
}
