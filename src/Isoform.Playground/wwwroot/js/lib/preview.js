// The preview of a drag (J2): while a handle is dragged, the edited document regenerates in the "preview" slot of a
// worker of its own, latest wins, and each result that comes back shows in store.preview, badged preview: true. The
// committed result (store.result) stays as it was; the release commits through commits.applyEdits, after which the
// preview is dropped. Pure (call() is the worker's regenerate), so Node tests it.
//
// Cells: as fine as keeps a preview within budget. A drag starts at the cell its handle last ran at in budget (at
// first: full resolution if the committed regeneration took less than the budget, else twice as coarse), and steps
// coarser while a reply takes longer. It never steps finer during a drag: the first reply at a new cell meshes
// every region again, which would bounce it back. The lattice is snapped to the world (snap), so successive previews
// do not shimmer and keep the cached regions the edit leaves alone.
import { adaptResult } from '../kernel-client.js';

/** ms a preview may take and still follow the pointer. */
export const budget = 120;

/** The preview cell is the committed cell times one of these, finest first. */
export const coarser = [1, 2, 4];

/**
 * The level (index in coarser) a handle's next drag starts at: the one its last drag ended on, a step finer when that
 * drag's previews ran in a fifth of the budget there (a finer cell has about four times the cells on a surface).
 * first: the level for a handle not dragged before.
 */
export function startLevel(last, first) {
  if (!last) return first;
  return last.level > 0 && last.slowest < budget / 5 ? last.level - 1 : last.level;
}

/**
 * call(args) regenerates in the preview worker and resolves to {result, ms}; settings() gives the committed
 * regeneration's {cell, chord}.
 *   begin(handle)   a drag starts: the worker regenerates the committed document unchanged (not shown), so its cache is
 *                   warm for the first move
 *   move(edits, t)  the edits at t; the newest wins, a reply is shown unless the drag has ended
 *   end()           the drag is over: replies still under way are not shown, the last preview stays until drop()
 *   drop()          clears store.preview
 *   timings         every preview reply of the page: {cell, ms, meshed, reused}
 */
export function createPreview(store, { call, settings }) {
  const last = new Map(); // handle id → {level, slowest}: how its last drag went
  const timings = [];
  let drag = null;        // {handle, document, level, slowest, warm: levels with a reply this drag}
  let busy = false, queued = null;

  function pump() {
    if (busy || !queued) return;
    const request = queued;
    queued = null;
    busy = true;
    call(request.args).then(reply => arrived(request, reply), () => { }) // a failed preview: the release reports it
      .finally(() => { busy = false; pump(); });
  }

  function send(edits, t, show) {
    const { cell, chord } = settings(), k = coarser[drag.level];
    queued = { drag, level: drag.level, t, show, args: { document: drag.document, edits, cell: cell * k, chord: chord * k, slot: 'preview', snap: true } };
    pump();
  }

  function arrived({ drag: mine, level, t, show, args }, reply) {
    const result = adaptResult(reply.result, reply.ms);
    if (result.error) return;
    timings.push({ cell: args.cell, ms: reply.ms, meshed: result.meshed, reused: result.reused });
    if (mine !== drag) return;
    // A cell's first reply meshes everything; only the replies after it say what a move costs there.
    if (mine.warm.has(level)) {
      if (level === mine.level) mine.slowest = Math.max(mine.slowest, reply.ms);
      if (reply.ms > budget && mine.level === level && level < coarser.length - 1) [mine.level, mine.slowest] = [level + 1, 0];
    }
    mine.warm.add(level);
    if (show) store.set({ preview: { ...result, preview: true, t, handle: mine.handle.id } });
  }

  return {
    timings,
    begin(handle) {
      const { result } = store.state;
      if (!result) return;
      const first = (result.timing?.regenerate ?? Infinity) < budget ? 0 : 1;
      drag = { handle, document: result.text, level: startLevel(last.get(handle.id), first), slowest: 0, warm: new Set() };
      send([], handle.kind === 'planar' ? [0, 0] : 0, false);
    },
    move(edits, t) {
      if (drag) send(edits, t, true);
    },
    end() {
      if (drag) last.set(drag.handle.id, { level: drag.level, slowest: drag.slowest });
      drag = null;
      queued = null;
    },
    drop() {
      if (store.state.preview) store.set({ preview: null });
    },
    /** The level (index in coarser) the drag under way previews at, or null. */
    level: () => drag?.level ?? null,
  };
}
