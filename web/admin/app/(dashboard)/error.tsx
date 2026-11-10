"use client";

// Production builds replace a server error's message with a generic one; the digest matches the
// server log line, so an operator can quote it.
export default function DashboardError({ error, reset }: { error: Error & { digest?: string }; reset: () => void }) {
  return (
    <section>
      <h1>Something went wrong</h1>
      <p className="error" role="alert">
        The page couldn&apos;t be loaded from the ledger API.
        {error.digest && (
          <>
            {" "}
            Reference <code>{error.digest}</code> (in the dashboard&apos;s log).
          </>
        )}
      </p>
      <button type="button" onClick={reset}>
        Try again
      </button>
    </section>
  );
}
