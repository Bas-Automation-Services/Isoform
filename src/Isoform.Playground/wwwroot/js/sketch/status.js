// What a solve says about each point and entity, for their colours, and the summary line. Pure, tested in Node.
// The reply is the sketch op's (src/Isoform.Interactive SketchJson): free (points and circles that can still move),
// freeDegrees, problems [{code, message, constraints: [indices], entities?: [ids]}].
import { pointsOf, refsOf } from './model.js';

/**
 * {state: Map(name → 'free' | 'fixed' | 'conflict') over points and entities, conflicts: Set of constraint indices}.
 * An entity is free when it or any of its points can move, in conflict when a constraint of the conflict names it or
 * it is a projected edge that is lost (sketch-reference-lost).
 */
export function states(sketch, reply) {
  const conflicts = new Set(reply.problems.filter(p => p.code === 'sketch-conflict').flatMap(p => p.constraints));
  const named = new Set([...conflicts].flatMap(i => sketch.constraints[i] ? refsOf(sketch.constraints[i]) : []));
  for (const p of reply.problems) if (p.code === 'sketch-reference-lost') for (const id of p.entities ?? []) named.add(id);
  const free = new Set(reply.free);
  const state = new Map();
  for (const p of Object.keys(sketch.points)) state.set(p, named.has(p) ? 'conflict' : free.has(p) ? 'free' : 'fixed');
  for (const e of sketch.entities)
    state.set(e.id, named.has(e.id) ? 'conflict' : free.has(e.id) || pointsOf(e).some(p => free.has(p)) ? 'free' : 'fixed');
  return { state, conflicts };
}

/** The status line: "3 free degrees", "fully constrained", or what is wrong. */
export function summary(reply) {
  if (reply.error) return { text: reply.error, level: 'error' };
  if (reply.problems.some(p => p.code === 'sketch-conflict')) return { text: 'constraints in conflict', level: 'error' };
  if (reply.problems.some(p => p.code === 'sketch-invalid')) return { text: 'invalid sketch', level: 'error' };
  if (reply.problems.some(p => p.code === 'sketch-reference-lost')) return { text: 'a projected edge is lost', level: 'error' };
  if (reply.freeDegrees === 0) return { text: 'fully constrained', level: 'ok' };
  return { text: `${reply.freeDegrees} free degree${reply.freeDegrees === 1 ? '' : 's'}`, level: 'free' };
}

/** "#3 length e2 = 40": a constraint as the panel and problems name it (as SketchSolver.Describe does). */
export function describeConstraint(c, i) {
  const kind = Object.keys(c).find(k => k !== 'value');
  return `#${i} ${kind} ${refsOf(c).join(' ')}${'value' in c ? ` = ${c.value}` : ''}`;
}
