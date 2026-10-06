// WOG Loot Log - read-only. Records every item the game reports as gained from combat
// (the same events behind its "Obtained ..." notices). Sends nothing to the server.
//   loot.list(n)   last n drops       loot.summary()  totals per item
//   loot.reset()   clear              loot.off()      unsubscribe
(() => {
  if (globalThis.loot) globalThis.loot.off();

  const notice = nn.services.notice;
  const itemData = nn.net.data.item;
  const startedAt = Date.now();
  const drops = [];                 // {t, tid, name, color, cnt}
  const totals = new Map();         // tid -> {tid, name, color, cnt, times}
  const stackBaseline = new Map();  // tid -> bigint count
  const knownNotStack = new Set();
  let inGainScope = false;

  const plainName = (rich) => String(rich ?? '').replace(/<[^>]*>/g, '');
  const colorOf = (rich) => (/<color=#?([0-9a-fA-F]{6})/.exec(String(rich ?? '')) || [])[1] || null;

  function record(tid, cnt) {
    const rich = notice.getColoredItemName ? notice.getColoredItemName(tid) : nn.services.item.getItemName(tid);
    const e = { t: Date.now(), tid: String(tid), name: plainName(rich) || ('#' + tid), color: colorOf(rich), cnt: Number(cnt) };
    drops.push(e);
    if (drops.length > 5000) drops.shift();
    const tot = totals.get(e.tid) || { tid: e.tid, name: e.name, color: e.color, cnt: 0, times: 0 };
    tot.cnt += e.cnt; tot.times++;
    totals.set(e.tid, tot);
  }

  const onStack = (itemTid) => {
    const cur = itemData.getItemStackCount(itemTid);
    const prev = stackBaseline.get(itemTid);
    stackBaseline.set(itemTid, cur);
    if (prev === undefined || !inGainScope) return;
    const diff = cur - prev;
    if (diff > 0n) record(itemTid, diff);
  };
  const onNotStack = (changed) => {
    for (const { itemId, itemTid } of changed) {
      if (!itemData.getItemNotStackUniqueId(itemId)) { knownNotStack.delete(itemId); continue; }
      if (knownNotStack.has(itemId)) continue;
      knownNotStack.add(itemId);
      if (inGainScope) record(itemTid, 1);
    }
  };

  // Mirror the game's combat-gain window so moves/sorts/shop purchases are not logged.
  const origScope = notice.runCombatGainScope;
  notice.runCombatGainScope = function (action) {
    inGainScope = true;
    try { return origScope.call(this, action); } finally { inGainScope = false; }
  };
  const hStack = nn.msgBroker.subscribe('onUpdateItemStack', onStack);
  const hNotStack = nn.msgBroker.subscribe('onUpdateItemNotStack', onNotStack);

  const fmtTime = (t) => new Date(t).toTimeString().slice(0, 8);

  globalThis.loot = {
    list(n = 20) {
      return drops.slice(-n).map(d => ({ time: fmtTime(d.t), item: d.name, cnt: d.cnt, tid: d.tid }));
    },
    summary() {
      const mins = Math.max(1 / 60, (Date.now() - startedAt) / 60000);
      return [...totals.values()]
        .sort((a, b) => b.cnt - a.cnt)
        .map(x => ({ item: x.name, cnt: x.cnt, perHour: Math.round(x.cnt / mins * 60), tid: x.tid, color: x.color }));
    },
    state() {
      return { since: startedAt, minutes: +((Date.now() - startedAt) / 60000).toFixed(1),
               drops: drops.length, recent: this.list(30), totals: this.summary() };
    },
    reset() { drops.length = 0; totals.clear(); return 'reset'; },
    off() {
      try { hStack.dispose(); } catch (e) {}
      try { hNotStack.dispose(); } catch (e) {}
      // The wrapper is an own property shadowing the prototype method; removing it restores the original.
      if (Object.prototype.hasOwnProperty.call(notice, 'runCombatGainScope')) delete notice.runCombatGainScope;
      return 'loot off';
    },
  };
  return 'loot log on';
})();
