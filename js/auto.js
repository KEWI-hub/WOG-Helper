// WOG automation - drives the game's own buttons on a timer. Every action goes through the
// same client functions the UI uses (and therefore the same server requests and checks).
//   auto.config({ equip: {...}, sort: {...}, training: {...} })   auto.state()   auto.off()
//
//   equip:    Enabled, IntervalSec  - equip the best wearable gear/accessory from the inventory
//   sort:     Enabled, IntervalSec  - sort the inventory (also clears the 'New' markers)
//   training: Enabled, ReserveGold, IntervalMs - level up the cheapest Training (gold only)
(() => {
  if (globalThis.auto) globalThis.auto.off();

  const TREE_TRAINING = 1;      // E_TreeType.PassiveSkillTree ("Training")
  const LOC_INVENTORY = 1;      // E_ItemLocation.Inventory
  const RESULT_SUCCESS = 1;     // E_NetResult.Success

  const cfg = {
    equip:    { Enabled: false, IntervalSec: 5 },
    sort:     { Enabled: false, IntervalSec: 60 },
    training: { Enabled: false, ReserveGold: 0, IntervalMs: 1000 },
  };
  const stats = { equips: 0, sorts: 0, trainings: 0, goldSpent: 0 };
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
  function gearScore(item, row) {
    const base = Number(row.EquipAbilityDetail) || 0;
    const ro = item?.randomOptions?.length ?? 0;
    return [base, ro, row.RatingType];
  }
  function better(a, b) {
    for (let i = 0; i < a.length; i++) if (a[i] !== b[i]) return a[i] > b[i];
    return false;
  }
  function findEquipUpgrade() {
    const eq = nn.services.equipment, im = nn.services.itemMove, data = nn.net.data.item;
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
      const preset = eq.getPresetByEquipType(cand.row.EquipType);
      if (!preset) continue;
      const slot = preset.getCurrentPresetItemSlot(cand.row.PartsType);
      if (slot?.slotLock ?? slot?._info?._slotLock) continue;
      const curTid = slot?.itemTid ?? 0;
      if (!curTid) return { preset, cand, current: null };
      const curRow = nn.db.equip.get(curTid);
      if (!curRow) continue;
      // Different base stat type (another class's item in the slot): only replace on grade.
      const curItem = data.getAllItemNotStack().find(x => x.itemId === slot.itemId);
      const curScore = gearScore(curItem, curRow);
      const sameStat = String(curRow.EquipAbilityType) === String(cand.row.EquipAbilityType);
      const wins = sameStat ? better(cand.score, curScore) : cand.row.RatingType > curRow.RatingType;
      if (wins) return { preset, cand, current: curRow };
    }
    return null;
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
  function findCheapestTraining() {
    const T = nn.services.tree;
    const goldTid = nn.db.config.gen.Gold_ItemID;
    const gold = Number(nn.services.item.getStackItemCount(goldTid));
    let best = null;
    for (const groupId of T.getGroupIds(TREE_TRAINING)) {
      if (!T.canEnchantGroup(TREE_TRAINING, groupId)) continue;
      const tid = T.getCurrentEnchantTreeId(TREE_TRAINING, groupId);
      const mats = T.getLevelUpMaterials(TREE_TRAINING, tid);
      if (mats.length === 0 || mats.some(m => m.itemTid !== goldTid)) continue;   // gold only
      const cost = mats.reduce((s, m) => s + Number(m.needCnt), 0);
      if (gold - cost < cfg.training.ReserveGold) continue;
      if (!best || cost < best.cost) best = { groupId, tid, cost };
    }
    return best;
  }
  async function trainingTick() {
    await exclusive(async () => {
      if (nn.services.unlockCondition.isLocked(E_CONTENT_TRAINING)) return;
      const pick = findCheapestTraining();
      if (!pick) return;
      const T = nn.services.tree;
      const before = T.getReachedLevel(TREE_TRAINING, pick.groupId);
      const ok = await T.reqEnchantAsync(TREE_TRAINING, pick.groupId);
      const row = T.getTable(TREE_TRAINING, pick.tid);
      const name = row ? nn.db.locale.getText(row.Name ?? '') : '#' + pick.groupId;
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

  // ---------------------------------------------------------------- scheduling
  function schedule(key, enabled, ms, fn) {
    if (timers[key]) { clearInterval(timers[key]); delete timers[key]; }
    if (enabled) timers[key] = setInterval(() => { fn().catch(e => note('error', String(e))); }, Math.max(200, ms));
  }
  function apply() {
    schedule('equip', cfg.equip.Enabled, cfg.equip.IntervalSec * 1000, equipTick);
    schedule('sort', cfg.sort.Enabled, cfg.sort.IntervalSec * 1000, sortTick);
    schedule('training', cfg.training.Enabled, cfg.training.IntervalMs, trainingTick);
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
    preview() {
      const up = findEquipUpgrade();
      const tr = findCheapestTraining();
      return {
        equip: up ? `${nn.services.item.getItemName(up.cand.item.itemTid)} -> part ${up.cand.row.PartsType}` : null,
        training: tr ? `group ${tr.groupId} cost ${tr.cost}` : null,
      };
    },
    off() {
      for (const k of Object.keys(timers)) { clearInterval(timers[k]); delete timers[k]; }
      return 'auto off';
    },
  };
  return 'auto ready';
})();
