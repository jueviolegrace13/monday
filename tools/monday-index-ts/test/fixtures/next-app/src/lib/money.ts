export const round = (value: number): number => Math.round(value * 100) / 100;

export function formatMoney(value: number): string {
  return `$${round(value)}`;
}
