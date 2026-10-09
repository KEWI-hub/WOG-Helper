// WOG automation - drives the game's own buttons on a timer. Every action goes through the
// same client functions the UI uses (and therefore the same server requests and checks).
//   auto.config({ equip, sort, training, raid, worldBoss, arena, fusion })   auto.fusionPlan()   auto.state()   auto.off()
//
//   equip:    Enabled, IntervalSec  - equip the best wearable gear/accessory from the inventory
//   sort:     Enabled, IntervalSec  - sort the inventory (also clears the 'New' markers)
//   training: Enabled, ReserveGold, IntervalMs, DamageFirst - level up Training (gold only):
//             best damage per gold first (saves up for it), otherwise the cheapest
//   raid / worldBoss: Enabled, IntervalSec - enter Battlefield Raid / World Boss while tickets last
//   arena:    Enabled, IntervalSec  - fight the best-value weaker opponent while tickets last
//   fusion:   Enabled, IntervalSec, MaxRating - Blacksmith fusion of spare bag items up to a grade
(() => {
  if (globalThis.auto) globalThis.auto.off();

  const TREE_TRAINING = 1;      // E_TreeType.PassiveSkillTree ("Training")
  const LOC_INVENTORY = 1;      // E_ItemLocation.Inventory
  const RESULT_SUCCESS = 1000;  // E_NetResult.Success (written as 1e3 in the bundle)

  const cfg = {
    equip:    { Enabled: false, IntervalSec: 5 },
    sort:     { Enabled: false, IntervalSec: 60 },
    training: { Enabled: false, ReserveGold: 0, IntervalMs: 1000, DamageFirst: true },
    raid:     { Enabled: false, IntervalSec: 10 },
    worldBoss: { Enabled: false, IntervalSec: 10 },
    arena:    { Enabled: false, IntervalSec: 10 },
    fusion:   { Enabled: false, IntervalSec: 30, MaxRating: 3 },
  };
  const stats = { equips: 0, sorts: 0, trainings: 0, goldSpent: 0, raid: 0, worldBoss: 0, arena: 0, fusions: 0 };
  const log = [];
  const timers = {};
  let busy = false;

  const now = () => new Date().toTimeString().slice(0, 8);
  function note(kind, text) {
    log.push({ time: now(), kind, text });
    if (log.length > 200) log.shift();
  }

  // One request at a time, and never while the game itself is mid-request or changing field.
  function gameIdle() {
    const session = nn.net.manager.gameSession;
    if (!session || session.hasAnyInFlightRequest) return false;   // boolean getter
    const fm = nn.services.combat.fieldManagerOrNull ?? nn.services.combat.fieldManager;
    if (fm?.isAnyFlowBusy?.()) return false;
    return true;
  }
  async function exclusive(fn) {
    if (busy || !gameIdle()) return;
    busy = true;
    try { await fn(); }
    catch (e) { note('error', String(e?.message ?? e)); }
    finally { busy = false; }
  }

  // ---------------------------------------------------------------- equip
  // Within one slot every item shares the same base stat type (e.g. gloves are all 1009), so
  // the base value is directly comparable. Ties: more random options, then higher grade.
  // Ability columns hold one value or a list (e.g. pauldrons "40,200": main stat + bonus stat).
  const columns = (v) => (v == null ? [] : Array.isArray(v) ? v : String(v).split(',')).map(Number);
  const mainStatType = (row) => columns(row.EquipAbilityType)[0] ?? 0;
  function gearScore(item, row) {
    const values = columns(row.EquipAbilityDetail).map(x => (Number.isFinite(x) ? x : 0));
    const main = values[0] ?? 0;
    const extra = values.slice(1).reduce((s, x) => s + x, 0);
    const ro = item?.randomOptions?.length ?? 0;
    return [main, extra, ro, row.RatingType];
  }
  function better(a, b) {
    for (let i = 0; i < a.length; i++) if (a[i] !== b[i]) return a[i] > b[i];
    return false;
  }
  function findEquipUpgrade() {
    const im = nn.services.itemMove, data = nn.net.data.item;
    const best = new Map();   // "equipType:part" -> {item,row,score}
    for (const item of data.getAllItemNotStack()) {
      if (item.location !== LOC_INVENTORY || item.isLock) continue;
      const row = nn.db.equip.get(item.itemTid);
      if (!row || !im.canEquipTo(item.itemTid, row.PartsType, item.itemId)) continue;
      const key = row.EquipType + ':' + row.PartsType;
      const score = gearScore(item, row);
      const cur = best.get(key);
      if (!cur || better(score, cur.score)) best.set(key, { item, row, score });
    }
    for (const cand of best.values()) {
      const cmp = compareWithEquipped(cand.item, cand.row);
      if (cmp && !cmp.slotLocked && cmp.wins) return { preset: cmp.preset, cand, current: cmp.current };
    }
    return null;
  }
  // Would this (wearable) item replace what the hero has in its slot?
  function compareWithEquipped(item, row) {
    const eq = nn.services.equipment;
    const preset = eq.getPresetByEquipType(row.EquipType);
    if (!preset) return null;
    const slot = preset.getCurrentPresetItemSlot(row.PartsType);
    const slotLocked = !!(slot?.slotLock ?? slot?._info?._slotLock);
    const curTid = slot?.itemTid ?? 0;
    if (!curTid) return { preset, slotLocked, current: null, wins: true };
    const curRow = nn.db.equip.get(curTid);
    if (!curRow) return null;
    // Worn item no longer fits the hero (class changed, level requirement): replace it.
    if (!eq.canWearByLevelAndClass(curTid, slot.itemId)) return { preset, slotLocked, current: curRow, wins: true };
    // Different main stat type (e.g. another weapon kind): only replace on grade.
    const curItem = nn.net.data.item.getAllItemNotStack().find(x => x.itemId === slot.itemId);
    const sameStat = mainStatType(curRow) === mainStatType(row);
    const wins = sameStat ? better(gearScore(item, row), gearScore(curItem, curRow)) : row.RatingType > curRow.RatingType;
    return { preset, slotLocked, current: curRow, wins };
  }
  async function equipTick() {
    await exclusive(async () => {
      // Several slots can improve at once; one per request, the next on the following passes.
      const up = findEquipUpgrade();
      if (!up) return;
      const { preset, cand, current } = up;
      const res = await preset.reqEquipCurrentPreset(cand.row.PartsType, cand.item.itemTid, cand.item.itemId);
      if (res?.NetResult === RESULT_SUCCESS) {
        stats.equips++;
        const from = current ? nn.services.item.getItemName(current.EquipID) : 'empty';
        note('equip', `${nn.services.item.getItemName(cand.item.itemTid)} (was ${from})`);
      } else {
        note('error', 'equip failed: ' + (res?.NetResult ?? 'no response'));
      }
    });
  }

  // ---------------------------------------------------------------- sort
  async function sortTick() {
    await exclusive(async () => {
      await nn.services.itemMove.sortInventory();
      stats.sorts++;
      note('sort', 'inventory sorted');
    });
  }

  // ---------------------------------------------------------------- training
  // Damage value of a level: the rise in the game's own offensive CP terms (attack, attack
  // speed, damage, boss damage, hit, crit, elements, block ignore, cooldown, end damage).
  const OFFENSE_CP = [166, 169, 171, 172, 173, 175, 177, 178, 179, 186, 207, 212];
  const PVP_ONLY = new Set([1065, 1067]);   // Duel / Combat Defense: no effect while farming
  const offenseCp = (C) => OFFENSE_CP.reduce((s, f) => s + Number(C.calcValue(f)), 0);
  // contentState.calcInstantValue overwrites the stat and then zeroes it, so it must not be
  // used. Add the delta, evaluate and put the old value back within this same JS turn.
  function offenseGain(type, delta) {
    const C = nn.services.contentState;
    const base = offenseCp(C);
    const cur = C.getValueToNumber(type);
    C.setValue(type, cur + delta);
    try { return offenseCp(C) - base; }
    finally { C.setValue(type, cur); }
  }

  // Candidates the hero could level now if gold allows (open, visible, not maxed).
  function trainingCandidates() {
    const T = nn.services.tree;
    const goldTid = nn.db.config.gen.Gold_ItemID;
    const out = [];
    for (const groupId of T.getGroupIds(TREE_TRAINING)) {
      const tid = T.getCurrentEnchantTreeId(TREE_TRAINING, groupId);
      if (tid <= 0 || !T.canDisplay(TREE_TRAINING, tid)) continue;
      const block = T.getEnchantBlock(TREE_TRAINING, tid);
      if (block !== 0 && block !== 5) continue;   // 5 = not enough materials yet
      const mats = T.getLevelUpMaterials(TREE_TRAINING, tid);
      if (mats.length === 0 || mats.some(m => m.itemTid !== goldTid)) continue;   // gold only
      const cost = mats.reduce((s, m) => s + Number(m.needCnt), 0);
      const next = T.getTable(TREE_TRAINING, tid);
      const reached = T.getReachedTable(TREE_TRAINING, groupId);
      const type = columns(next.AbilityType)[0];
      const delta = (columns(next.AbilityDetailCnt)[0] || 0) - (reached ? columns(reached.AbilityDetailCnt)[0] || 0 : 0);
      out.push({ groupId, tid, cost, type, delta, name: nn.db.locale.getText(next.Name ?? '') });
    }
    return out;
  }

  function findTraining() {
    const gold = Number(nn.services.item.getStackItemCount(nn.db.config.gen.Gold_ItemID));
    const affordable = (c) => gold - c.cost >= cfg.training.ReserveGold;
    const all = trainingCandidates();
    if (cfg.training.DamageFirst) {
      for (const c of all) c.gain = PVP_ONLY.has(c.type) || c.delta <= 0 ? 0 : offenseGain(c.type, c.delta);
      const dmg = all.filter(c => c.gain > 0).sort((a, b) => b.gain / b.cost - a.gain / a.cost || a.cost - b.cost);
      // Save up for the best damage per gold instead of spending on a weaker one.
      if (dmg.length) return affordable(dmg[0]) ? dmg[0] : null;
    }
    // Cheapest first (also once nothing left adds damage).
    return all.filter(affordable).sort((a, b) => a.cost - b.cost)[0] ?? null;
  }
  async function trainingTick() {
    await exclusive(async () => {
      if (nn.services.unlockCondition.isLocked(E_CONTENT_TRAINING)) return;
      const pick = findTraining();
      if (!pick) return;
      const T = nn.services.tree;
      if (!T.canEnchantGroup(TREE_TRAINING, pick.groupId)) return;
      const before = T.getReachedLevel(TREE_TRAINING, pick.groupId);
      const ok = await T.reqEnchantAsync(TREE_TRAINING, pick.groupId);
      const name = pick.name || '#' + pick.groupId;
      if (ok) {
        stats.trainings++;
        stats.goldSpent += pick.cost;
        note('training', `${name} Lv${before} -> Lv${before + 1} (-${pick.cost.toLocaleString()} gold)`);
      } else {
        note('error', `training ${name} failed`);
      }
    });
  }
  const E_CONTENT_TRAINING = 13;   // E_ContentType.Training

  // ---------------------------------------------------------------- battlefield
  // Raid, World Boss and Arena go through the same startDungeonBattle as the Battlefield
  // buttons. The server fights the whole battle; the result panel returns to the stage by itself
  // after its countdown, then the next ticket goes. One battle at a time for all three.
  const LEAGUE = { arena: 1, worldBoss: 3, raid: 4 };   // E_LeagueType
  const SEASON_OPEN = 2;           // E_LeagueSeasonState.Open
  const TRY_ENTER = 0;             // E_DungeonTryType.Enter
  const ENTER_SUCCESS = 1;         // E_FieldEnterResult.Success
  const LABEL = { raid: 'raid', worldBoss: 'world boss', arena: 'arena' };
  const blockedNote = {};
  function blocked(kind, why) {
    if (blockedNote[kind] !== why) note(kind, `${LABEL[kind]}: ${why}`);   // say it once, not every tick
    blockedNote[kind] = why;
  }
  function battlefieldFree() {
    const fm = nn.services.combat.fieldManagerOrNull;
    return !!fm && !fm.hasDungeon && !fm.isDungeonFlowBusy;   // not in a battle, not leaving one
  }
  async function openLeagueTid(kind) {
    const league = nn.services.league;
    let leagueTid = league.getCurrentLeagueTidByLeagueType(LEAGUE[kind]);
    if (leagueTid <= 0) {
      await league.reqLeagueSeasonInfo();
      leagueTid = league.getCurrentLeagueTidByLeagueType(LEAGUE[kind]);
    }
    if (leagueTid <= 0) { blocked(kind, 'no season'); return 0; }
    if (league.getStrLeagueSeasonRemainTime02(leagueTid)[0] !== SEASON_OPEN) { blocked(kind, 'season is not open'); return 0; }
    return leagueTid;
  }
  function hasTicket(kind, table) {
    if (table.EntryItemID > 0 && !nn.services.item.hasEnoughStackItem(table.EntryItemID, table.EntryItemCnt, false)) {
      blocked(kind, 'out of tickets');
      return false;
    }
    return true;
  }
  async function enter(kind, table, target) {
    blockedNote[kind] = '';
    const left = Number(nn.services.item.getStackItemCount(table.EntryItemID));
    const res = await nn.services.dungeon.startDungeonBattle(table.DungeonID, target, TRY_ENTER);
    if (res === ENTER_SUCCESS) {
      stats[kind]++;
      const vs = target ? ` vs ${target.nick} (CP ${Number(target.combatPower).toLocaleString()})` : '';
      note(kind, `entered ${LABEL[kind]}${vs} (${left - table.EntryItemCnt} tickets left)`);
    } else {
      note('error', `${LABEL[kind]} enter failed: ${res}`);
    }
  }
  // Raid / World Boss: the Battlefield tab's Enter button.
  function leagueDungeonTick(kind) {
    return async () => {
      if (!battlefieldFree()) return;
      await exclusive(async () => {
        if (!(await openLeagueTid(kind))) return;
        const table = nn.services.league.getEnterDungeonTable(LEAGUE[kind]);
        if (!table) return blocked(kind, 'not available');
        if (!hasTicket(kind, table)) return;
        await enter(kind, table, undefined);
      });
    };
  }
  // Arena: pick from the challenge list the server offers. Among opponents with lower CP take
  // the one worth the most points; if everyone is stronger, take the weakest.
  async function arenaTick() {
    if (!battlefieldFree()) return;
    await exclusive(async () => {
      if (!(await openLeagueTid('arena'))) return;
      const table = nn.db.dungeon.get(nn.db.config.Dungeon_Arena_Single_Server_ID);
      if (!table) return blocked('arena', 'not available');
      if (!hasTicket('arena', table)) return;
      const res = await nn.net.manager.gameSession.reqArenaPvpUserInfo(table.DungeonID);
      const info = !res || res.hasError() ? null : res.Data.arenaPvpUserInfo();
      if (!info) return blocked('arena', 'could not load the challenge list');
      // Raw flatbuffer rows; same fields the game's NetServerRankInfo copies out.
      const list = [];
      for (let i = 0; i < info.serverRankInfosLength(); i++) {
        const raw = info.serverRankInfos(i);
        if (!raw) continue;
        const r = { rank: raw.rank(), userId: raw.userId(), nick: raw.nick() ?? '', lv: raw.lv(),
                    combatPower: raw.combatPower(), guildType: raw.guildType(), score: raw.score(),
                    isMatchUser: raw.isMatchUser() };
        list.push({ r, cp: Number(r.combatPower), win: nn.db.arena.getByListOrder(i + 1)?.WinScore ?? 0 });
      }
      if (!list.length) return blocked('arena', 'empty challenge list');
      const myCp = nn.services.contentState.finalCp.toNumber();
      const weaker = list.filter(x => x.cp < myCp).sort((a, b) => b.win - a.win || a.cp - b.cp);
      const pick = weaker[0] ?? list.slice().sort((a, b) => a.cp - b.cp)[0];
      await enter('arena', table, pick.r);
    });
  }

  // ---------------------------------------------------------------- blacksmith (fusion)
  // N items of one grade and level band fuse into a random item of a higher grade. Only bag
  // items are used: never storage, locked, equipped items, or gear that would beat what the
  // hero wears (Auto Equip wants those). Fewest random options go first, like the game.
  const RATING_NAMES = ['', 'Normal', 'Magic', 'Rare', 'Hero', 'Legend', 'Myth', 'Ancient', 'Primordial', 'Transcendent', 'Divine'];
  function isKeeper(item, row) {
    if (!row || !nn.services.itemMove.canEquipTo(item.itemTid, row.PartsType, item.itemId)) return false;
    return !!compareWithEquipped(item, row)?.wins;
  }
  function fusionPlan() {
    const W = nn.services.workshop, itemMove = nn.services.itemMove;
    const groups = new Map();   // FusionID -> {fusion, items[], kept[]}
    for (const item of nn.net.data.item.getAllItemNotStack()) {
      if (item.location !== LOC_INVENTORY || item.isLock || itemMove.isEquippedItemId(item.itemId)) continue;
      const fusion = W.resolveFusionTableByItem(item.itemTid, item.itemId);
      if (!fusion || !W.canUseAsFusionMaterial(fusion, item.itemTid, item.itemId)) continue;
      const g = groups.get(fusion.FusionID) ?? { fusion, items: [], kept: [] };
      (isKeeper(item, nn.db.equip.get(item.itemTid)) ? g.kept : g.items).push(item);
      groups.set(fusion.FusionID, g);
    }
    return [...groups.values()];
  }
  async function fusionTick() {
    await exclusive(async () => {
      const W = nn.services.workshop;
      const ready = fusionPlan()
        .filter(g => g.fusion.MaterialRating <= cfg.fusion.MaxRating && g.items.length >= g.fusion.MaterialRatingCnt)
        .sort((a, b) => a.fusion.MaterialRating - b.fusion.MaterialRating);
      const g = ready[0];
      if (!g) return;
      const ro = (it) => it.randomOptions?.length ?? 0;
      const pick = g.items.slice().sort((a, b) => ro(a) - ro(b)).slice(0, g.fusion.MaterialRatingCnt);
      const materials = pick.map(it => ({ itemId: it.itemId, itemTid: it.itemTid, itemCnt: 1 }));
      const names = pick.map(it => nn.services.item.getItemName(it.itemTid));
      const res = await W.reqFusionAsync(g.fusion.FusionID, materials);
      if (res?.NetResult === RESULT_SUCCESS) {
        stats.fusions++;
        let got = '';
        try { got = W.buildFusionRewards(res.Data, g.fusion.ContentType).map(r => nn.services.item.getItemName(r.tid)).join(', '); } catch (e) {}
        note('fusion', `${RATING_NAMES[g.fusion.MaterialRating]} x${pick.length} (${names.join(', ')}) -> ${got || 'done'}`);
      } else {
        note('error', 'fusion failed: ' + (res?.NetResult ?? 'no response'));
      }
    });
  }

  // ---------------------------------------------------------------- scheduling
  function schedule(key, enabled, ms, fn) {
    if (timers[key]) { clearInterval(timers[key]); delete timers[key]; }
    if (enabled) timers[key] = setInterval(() => { fn().catch(e => note('error', String(e))); }, Math.max(200, ms));
  }
  function apply() {
    schedule('equip', cfg.equip.Enabled, cfg.equip.IntervalSec * 1000, equipTick);
    schedule('sort', cfg.sort.Enabled, cfg.sort.IntervalSec * 1000, sortTick);
    schedule('training', cfg.training.Enabled, cfg.training.IntervalMs, trainingTick);
    schedule('raid', cfg.raid.Enabled, cfg.raid.IntervalSec * 1000, leagueDungeonTick('raid'));
    schedule('worldBoss', cfg.worldBoss.Enabled, cfg.worldBoss.IntervalSec * 1000, leagueDungeonTick('worldBoss'));
    schedule('arena', cfg.arena.Enabled, cfg.arena.IntervalSec * 1000, arenaTick);
    schedule('fusion', cfg.fusion.Enabled, cfg.fusion.IntervalSec * 1000, fusionTick);
  }

  globalThis.auto = {
    config(c) {
      for (const k of Object.keys(cfg)) if (c && c[k]) Object.assign(cfg[k], c[k]);
      apply();
      return cfg;
    },
    state(n = 30) {
      return { cfg, stats, gold: String(nn.services.item.getStackItemCount(nn.db.config.gen.Gold_ItemID)),
               log: log.slice(-n) };
    },
    fusionPlan() {
      return fusionPlan().map(g => ({
        rating: g.fusion.MaterialRating, grade: RATING_NAMES[g.fusion.MaterialRating] ?? String(g.fusion.MaterialRating),
        level: g.fusion.MaterialLevelLimit, need: g.fusion.MaterialRatingCnt, have: g.items.length, kept: g.kept.length,
        items: g.items.map(it => nn.services.item.getItemName(it.itemTid)),
      })).sort((a, b) => b.have / b.need - a.have / a.need || a.rating - b.rating);
    },
    preview() {
      const up = findEquipUpgrade();
      const tr = findTraining();
      return {
        equip: up ? `${nn.services.item.getItemName(up.cand.item.itemTid)} -> part ${up.cand.row.PartsType}` : null,
        training: tr ? `${tr.name} cost ${tr.cost}${tr.gain ? " dmgCP +" + tr.gain.toFixed(1) : ""}` : null,
      };
    },
    off() {
      for (const k of Object.keys(timers)) { clearInterval(timers[k]); delete timers[k]; }
      return 'auto off';
    },
  };
  return 'auto ready';
})();
