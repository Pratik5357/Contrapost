import "server-only";
import { createHmac, randomUUID, scryptSync, timingSafeEqual } from "node:crypto";
import { cookies } from "next/headers";

// Operator login (design doc §5.2): one operator from the environment, and an HTTP-only session cookie
// signed with HMAC-SHA256. The cookie carries a random session id that every API call passes on in
// X-Operator-Session, so the API's audit log can tell sessions apart.

const COOKIE = "cp_admin_session";
const LIFETIME_SECONDS = 8 * 60 * 60;

export type Session = { sid: string; user: string; guest: boolean; exp: number };

function secret(): string {
  const value = process.env.SESSION_SECRET;
  if (!value || value.length < 32) {
    throw new Error("SESSION_SECRET must be set to at least 32 characters.");
  }
  return value;
}

function sign(payload: string): string {
  return createHmac("sha256", secret()).update(payload).digest("base64url");
}

function encode(session: Session): string {
  const payload = Buffer.from(JSON.stringify(session)).toString("base64url");
  return `${payload}.${sign(payload)}`;
}

function decode(value: string | undefined): Session | null {
  const [payload, signature] = value?.split(".") ?? [];
  if (!payload || !signature) return null;
  const expected = Buffer.from(sign(payload));
  const actual = Buffer.from(signature);
  if (expected.length !== actual.length || !timingSafeEqual(expected, actual)) return null;
  try {
    const session = JSON.parse(Buffer.from(payload, "base64url").toString()) as Session;
    return session.exp * 1000 > Date.now() ? session : null;
  } catch {
    return null;
  }
}

export async function getSession(): Promise<Session | null> {
  return decode((await cookies()).get(COOKIE)?.value);
}

export async function startSession(user: string, guest: boolean): Promise<void> {
  const session: Session = { sid: randomUUID(), user, guest, exp: Math.floor(Date.now() / 1000) + LIFETIME_SECONDS };
  (await cookies()).set(COOKIE, encode(session), {
    httpOnly: true,
    sameSite: "lax",
    // Off only for the local stack, which serves plain http on a non-localhost name in CI.
    secure: process.env.SESSION_COOKIE_SECURE !== "false",
    path: "/",
    maxAge: LIFETIME_SECONDS,
  });
}

export async function endSession(): Promise<void> {
  (await cookies()).delete(COOKIE);
}

/** ADMIN_PASSWORD_HASH is `scrypt:<salt>:<hash>` in base64url, from `npm run hash-password`. */
export function checkCredentials(user: string, password: string): boolean {
  const [scheme, salt, hash] = (process.env.ADMIN_PASSWORD_HASH ?? "").split(":");
  if (scheme !== "scrypt" || !salt || !hash) return false;
  const expected = Buffer.from(hash, "base64url");
  const actual = scryptSync(password, Buffer.from(salt, "base64url"), expected.length);
  // Compare both, always, so timing doesn't reveal whether the username was right.
  const userOk = safeEqual(user, process.env.ADMIN_USERNAME ?? "");
  return timingSafeEqual(expected, actual) && userOk;
}

function safeEqual(a: string, b: string): boolean {
  const left = createHmac("sha256", "compare").update(a).digest();
  const right = createHmac("sha256", "compare").update(b).digest();
  return timingSafeEqual(left, right);
}

export function guestModeEnabled(): boolean {
  return process.env.ADMIN_GUEST_MODE === "true";
}
