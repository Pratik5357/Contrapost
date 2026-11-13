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

const relativeFormat = new Intl.RelativeTimeFormat("en", { numeric: "auto" });
const units: [Intl.RelativeTimeFormatUnit, number][] = [
  ["day", 86_400],
  ["hour", 3_600],
  ["minute", 60],
  ["second", 1],
];

/** "3 min ago", "in 2 hours": how people think about recent times. The exact time goes in a tooltip. */
export function relative(iso: string, now = Date.now()): string {
  const seconds = Math.round((Date.parse(iso) - now) / 1000);
  if (Math.abs(seconds) < 10) return "just now";
  const [unit, size] = units.find(([, size]) => Math.abs(seconds) >= size) ?? units[units.length - 1];
  return relativeFormat.format(Math.trunc(seconds / size), unit);
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
