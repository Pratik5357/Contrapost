import Link from "next/link";
import { redirect } from "next/navigation";
import { getSession } from "@/lib/session";
import { logOut } from "../login/actions";

const pages = [
  { href: "/", label: "Overview" },
  { href: "/accounts", label: "Accounts" },
  { href: "/holds", label: "Holds" },
  { href: "/reconciliation", label: "Reconciliation" },
  { href: "/outbox", label: "Outbox" },
];

// Every page under here needs a session; the pages themselves only read through /v1/admin/*.
export default async function DashboardLayout({ children }: { children: React.ReactNode }) {
  const session = await getSession();
  if (!session) redirect("/login");

  return (
    <>
      <header className="topbar">
        <Link href="/" className="brand">
          Contrapost
        </Link>
        <nav>
          {pages.map((page) => (
            <Link key={page.href} href={page.href}>
              {page.label}
            </Link>
          ))}
          {process.env.GRAFANA_URL && (
            <a href={process.env.GRAFANA_URL} target="_blank" rel="noreferrer">
              Metrics ↗
            </a>
          )}
        </nav>
        <form action={logOut} className="session">
          <span className="muted">{session.guest ? "guest" : session.user}</span>
          <button type="submit" className="link">
            Log out
          </button>
        </form>
      </header>
      <main className="content">{children}</main>
    </>
  );
}
