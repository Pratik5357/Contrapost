import Link from "next/link";
import { dateTime, label, relative, short } from "@/lib/format";

const tones: Record<string, string> = {
  active: "good",
  clean: "good",
  captured: "good",
  posted: "good",
  pending: "warn",
  partially_captured: "warn",
  running: "warn",
  frozen: "warn",
  drift_found: "bad",
  failed: "bad",
  closed: "muted",
  voided: "muted",
  expired: "muted",
};

export function Badge({ value }: { value: string }) {
  return <span className={`badge ${tones[value] ?? ""}`}>{label(value)}</span>;
}

export function Id({ href, id }: { href?: string; id: string }) {
  const text = <code title={id}>…{short(id)}</code>;
  return href ? <Link href={href}>{text}</Link> : text;
}

/** Keyset paging is forward-only: a link to the first page, and one to the next when there is one. */
export function Pager({ basePath, params, next }: { basePath: string; params: Record<string, string | undefined>; next: string | null }) {
  const query = (after?: string) => {
    const search = new URLSearchParams();
    for (const [key, value] of Object.entries(params)) if (value && key !== "after") search.set(key, value);
    if (after) search.set("after", after);
    const text = search.toString();
    return text ? `${basePath}?${text}` : basePath;
  };
  return (
    <nav className="pager" aria-label="Pages">
      {params.after && <Link href={query()}>← First page</Link>}
      {next && <Link href={query(next)}>Next page →</Link>}
    </nav>
  );
}

export function Empty({ children }: { children: React.ReactNode }) {
  return <p className="muted empty">{children}</p>;
}

/** A relative time ("5 min ago"), with the exact time on hover and for screen readers' context. */
export function Time({ iso, fallback = "—" }: { iso: string | null; fallback?: string }) {
  if (!iso) return <span className="muted">{fallback}</span>;
  return (
    <time dateTime={iso} title={dateTime(iso)}>
      {relative(iso)}
    </time>
  );
}

/** "12 shown" plus a way back to the unfiltered list, next to a page's filters. */
export function ResultSummary({ count, more, filtered, clearHref }: { count: number; more: boolean; filtered: boolean; clearHref: string }) {
  return (
    <p className="result-summary muted">
      {count.toLocaleString("en-IN")} shown{more ? ", more on the next page" : ""}
      {filtered && (
        <>
          {" · "}
          <Link href={clearHref}>Clear filters</Link>
        </>
      )}
    </p>
  );
}
