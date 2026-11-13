"use client";

import Link from "next/link";
import { usePathname } from "next/navigation";

const pages = [
  { href: "/", label: "Overview" },
  { href: "/accounts", label: "Accounts" },
  { href: "/holds", label: "Holds" },
  { href: "/reconciliation", label: "Reconciliation" },
  { href: "/outbox", label: "Outbox" },
];

/** The top navigation, marking the section you're in (an account page counts as Accounts). */
export function NavLinks({ grafanaUrl }: { grafanaUrl?: string }) {
  const path = usePathname();
  const current = (href: string) => (href === "/" ? path === "/" : path === href || path.startsWith(`${href}/`));

  return (
    <nav className="nav" aria-label="Sections">
      {pages.map((page) => (
        <Link key={page.href} href={page.href} aria-current={current(page.href) ? "page" : undefined}>
          {page.label}
        </Link>
      ))}
      {grafanaUrl && (
        <a href={grafanaUrl} target="_blank" rel="noreferrer">
          Metrics ↗
        </a>
      )}
    </nav>
  );
}
