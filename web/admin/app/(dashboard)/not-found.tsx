import Link from "next/link";

export default function NotFound() {
  return (
    <section>
      <h1>Not found</h1>
      <p className="muted">No such record in the ledger.</p>
      <Link href="/">Back to the overview</Link>
    </section>
  );
}
