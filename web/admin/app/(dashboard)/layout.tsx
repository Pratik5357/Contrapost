import Link from "next/link";
import { redirect } from "next/navigation";
import { NavLinks } from "@/components/NavLinks";
import { getSession } from "@/lib/session";
import { logOut } from "../login/actions";

// Every page under here needs a session; the pages themselves only read through /v1/admin/*.
export default async function DashboardLayout({ children }: { children: React.ReactNode }) {
  const session = await getSession();
  if (!session) redirect("/login");

  return (
    <>
      <a href="#content" className="skip-link">
        Skip to content
      </a>
      <header className="topbar">
        <Link href="/" className="brand">
          <span className="brand-mark" aria-hidden="true" />
          Contrapost
        </Link>
        <NavLinks grafanaUrl={process.env.GRAFANA_URL} />
        <form action={logOut} className="session">
          <span className="muted">{session.guest ? "Guest (read-only)" : session.user}</span>
          <button type="submit" className="ghost">
            Log out
          </button>
        </form>
      </header>
      <main id="content" className="content">
        {children}
      </main>
    </>
  );
}
