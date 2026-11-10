// Amounts arrive as integers in minor units (design doc §4); the currency decides the decimals.

const formatters = new Map<string, Intl.NumberFormat>();

function formatter(currency: string): Intl.NumberFormat {
  let format = formatters.get(currency);
  if (!format) {
    format = new Intl.NumberFormat("en-IN", { style: "currency", currency });
    formatters.set(currency, format);
  }
  return format;
}

export function money(minor: number, currency: string): string {
  const format = formatter(currency);
  const digits = format.resolvedOptions().maximumFractionDigits ?? 2;
  return format.format(minor / 10 ** digits);
}

export function dateTime(iso: string | null): string {
  if (!iso) return "—";
  return new Date(iso).toLocaleString("en-IN", { dateStyle: "medium", timeStyle: "medium", hour12: false });
}

export function duration(seconds: number): string {
  if (seconds < 60) return `${seconds}s`;
  if (seconds < 3600) return `${Math.floor(seconds / 60)}m ${seconds % 60}s`;
  return `${Math.floor(seconds / 3600)}h ${Math.floor((seconds % 3600) / 60)}m`;
}

/** UUIDv7 ids start with a timestamp, so the random tail is what tells them apart. */
export function short(id: string): string {
  return id.slice(-8);
}

export function label(value: string): string {
  return value.replaceAll("_", " ");
}
