import { redirect } from "next/navigation";
import { getSession, guestModeEnabled } from "@/lib/session";
import { logIn, continueAsGuest } from "./actions";

export const metadata = { title: "Log in" };

export default async function LoginPage({ searchParams }: { searchParams: Promise<{ error?: string }> }) {
  if (await getSession()) redirect("/");
  const { error } = await searchParams;

  return (
    <main className="login">
      <h1>Contrapost admin</h1>
      <p className="muted">Read-only operator console for the ledger.</p>
      <form action={logIn} className="stack">
        <label>
          Username
          <input name="username" autoComplete="username" required />
        </label>
        <label>
          Password
          <input name="password" type="password" autoComplete="current-password" required />
        </label>
        {error && (
          <p className="error" role="alert">
            Wrong username or password.
          </p>
        )}
        <button type="submit">Log in</button>
      </form>
      {guestModeEnabled() && (
        <form action={continueAsGuest}>
          <button type="submit" className="secondary">
            Continue as guest
          </button>
        </form>
      )}
    </main>
  );
}
