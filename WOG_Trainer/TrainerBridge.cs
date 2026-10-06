using System.IO.MemoryMappedFiles;
using System.Text;

namespace WOG_Trainer;

/// <summary>
/// Shared-memory channel to the injected WOGHook.dll.
/// Layout MUST match SharedState in WOGHook/dllmain.cpp (pack=1).
/// </summary>
internal sealed class TrainerBridge : IDisposable
{
    private const string MapName = "WOGTrainerShared2";
    private const int    Magic   = 0x32474F57; // "WOG2"

    private const long OffMagic         = 0x00;
    private const long OffHeartbeat     = 0x04;
    private const long OffStatus        = 0x14;
    private const long OffTrainerPaused = 0x18;
    private const long OffFrameCount    = 0x1C;
    private const long OffCmdRequest    = 0x2C;
    private const long OffCmdDone       = 0x30;
    private const long OffCmdType       = 0x34;
    private const long OffCmdStatus     = 0x38;
    private const long OffCmdLength     = 0x3C;
    private const long OffText          = 0x40 + 256 + (16 << 10);
    private const int  TextSize         = 1 << 20;
    private const int  MapSize          = (int)OffText + TextSize;

    private const int CmdEvalJs = 1;

    private MemoryMappedFile?         _mmf;
    private MemoryMappedViewAccessor? _view;

    public bool Connect(int timeoutMs)
    {
        var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
        while (DateTime.UtcNow < deadline)
        {
            try
            {
                _mmf  = MemoryMappedFile.OpenExisting(MapName, MemoryMappedFileRights.ReadWrite);
                _view = _mmf.CreateViewAccessor(0, MapSize, MemoryMappedFileAccess.ReadWrite);
                if (_view.ReadInt32(OffMagic) == Magic)
                    return true;
                _view.Dispose(); _view = null;
                _mmf.Dispose();  _mmf  = null;
            }
            catch { /* hook has not created the map yet */ }
            Thread.Sleep(100);
        }
        return false;
    }

    public int Heartbeat  => _view?.ReadInt32(OffHeartbeat) ?? 0;
    public int Status     => _view?.ReadInt32(OffStatus) ?? 0;
    public int FrameCount => _view?.ReadInt32(OffFrameCount) ?? 0;

    public void SetPaused(bool paused) => _view?.Write(OffTrainerPaused, paused ? 1 : 0);

    /// <summary>
    /// Evaluates JS on the game's main thread. Returns the value (strings as-is, anything else
    /// JSON) or throws with the JS error. The game must be running frames for this to finish.
    /// </summary>
    public string EvalJs(string code, int timeoutMs = 3000)
    {
        if (_view == null) throw new InvalidOperationException("Not connected");
        if (_view.ReadInt32(OffCmdRequest) != _view.ReadInt32(OffCmdDone))
            throw new TimeoutException("Previous command still running");

        byte[] bytes = Encoding.UTF8.GetBytes(code + "\0");
        if (bytes.Length > TextSize) throw new ArgumentException("Script too long");
        _view.WriteArray(OffText, bytes, 0, bytes.Length);
        _view.Write(OffCmdType, CmdEvalJs);
        int req = _view.ReadInt32(OffCmdRequest) + 1;
        _view.Write(OffCmdRequest, req);

        var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
        while (_view.ReadInt32(OffCmdDone) != req)
        {
            if (DateTime.UtcNow > deadline) throw new TimeoutException("Game did not run the script (paused or loading?)");
            Thread.Sleep(15);
        }

        int len = Math.Clamp(_view.ReadInt32(OffCmdLength), 0, TextSize);
        var buf = new byte[len];
        _view.ReadArray(OffText, buf, 0, len);
        string text = Encoding.UTF8.GetString(buf);
        // V8 appends the source position to thrown values: "R:<value> at (file : line : col)".
        int at = text.LastIndexOf(" at (", StringComparison.Ordinal);
        if (at >= 0) text = text[..at];
        text = text.TrimEnd();

        if (_view.ReadInt32(OffCmdStatus) == 1 && text.StartsWith("R:", StringComparison.Ordinal))
            return text[2..];
        throw new InvalidOperationException(text.StartsWith("E:", StringComparison.Ordinal) ? text[2..] : text);
    }

    public void Dispose()
    {
        _view?.Dispose(); _view = null;
        _mmf?.Dispose();  _mmf  = null;
    }
}
