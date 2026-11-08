import { SenderError } from 'spacetimedb/server';
import type { Ctx } from './schema';
import { currentYear, recompute } from './engine';
import { sensorFromCondition } from './seed';

export function createWorkOrderTx(ctx: Ctx, componentId: number, action: string, source: string, byName: string) {
  const c = ctx.db.component.id.find(componentId);
  if (!c) throw new SenderError('Unknown component');
  const open = [...ctx.db.workOrder.blueprintId.filter(c.blueprintId)].find(w => w.componentId === componentId && w.status !== 'done');
  if (open) return open;
  const risk = ctx.db.componentRisk.componentId.find(componentId);
  return ctx.db.workOrder.insert({
    id: 0n, blueprintId: c.blueprintId, componentId, title: `${c.name}: ${action}`.slice(0, 120), action,
    cost: c.replaceCost, status: 'dispatched', createdByName: byName, source,
    lossAvoided: risk?.preventionValue ?? 0, createdAt: ctx.timestamp,
  });
}

export function completeWorkOrder(ctx: Ctx, wo: { componentId: number; blueprintId: number }) {
  const c = ctx.db.component.id.find(wo.componentId);
  if (c) {
    ctx.db.component.id.update({
      ...c, condition: 1, installYear: currentYear(ctx), hazardFlag: '', sensorValue: sensorFromCondition(c.kind, 1),
    });
    const s = ctx.db.simulationState.id.find(1);
    ctx.db.alert.insert({
      id: 0n, blueprintId: wo.blueprintId, componentId: wo.componentId, level: 'info',
      message: `Work order complete — ${c.name} restored. Cascade risk recalculated.`, simMonth: s?.simMonths ?? 0, at: ctx.timestamp,
    });
  }
  recompute(ctx, wo.blueprintId);
}
