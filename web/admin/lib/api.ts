import "server-only";
import { getSession } from "./session";

// Server-side only: the dashboard's admin:read key lives in this process's environment and is never
// sent to the browser. Every call carries the operator's session id for the API's audit log.

export class ApiError extends Error {
  constructor(
    readonly status: number,
    readonly title: string,
    readonly detail: string,
  ) {
    super(`${status} ${title}${detail ? `: ${detail}` : ""}`);
  }
}

type Query = Record<string, string | number | undefined | null>;

/** GET an admin endpoint. Returns null on 404; throws ApiError on any other failure. */
export async function adminGet<T>(path: string, query: Query = {}): Promise<T | null> {
  const url = new URL(`/v1/admin${path}`, process.env.ADMIN_API_URL ?? "http://localhost:8080");
  for (const [key, value] of Object.entries(query)) {
    if (value !== undefined && value !== null && value !== "") url.searchParams.set(key, String(value));
  }

  const session = await getSession();
  const response = await fetch(url, {
    headers: {
      Authorization: `Bearer ${process.env.ADMIN_API_KEY ?? ""}`,
      "X-Operator-Session": session?.sid ?? "none",
    },
    cache: "no-store", // balances are always read fresh (design doc §5.2)
  });

  if (response.status === 404) return null;
  if (!response.ok) {
    const problem = (await response.json().catch(() => ({}))) as { title?: string; detail?: string; errors?: Record<string, string[]> };
    const fields = problem.errors ? Object.entries(problem.errors).map(([field, messages]) => `${field}: ${messages.join(" ")}`).join("; ") : "";
    throw new ApiError(response.status, problem.title ?? response.statusText, fields || problem.detail || "");
  }
  return (await response.json()) as T;
}

export type Filtered<T> = { data: T; invalid: null } | { data: null; invalid: string };

/**
 * For list pages whose filters come from the URL: a 400 (a filter the API rejects) comes back as a
 * message to show next to the form, instead of failing the page. Production builds hide server error
 * messages from the browser, so a thrown error could only say "something went wrong".
 */
export async function adminList<T>(path: string, query: Query = {}): Promise<Filtered<T>> {
  try {
    return { data: (await adminGet<T>(path, query))!, invalid: null };
  } catch (error) {
    if (error instanceof ApiError && error.status === 400) return { data: null, invalid: error.detail || error.title };
    throw error;
  }
}
