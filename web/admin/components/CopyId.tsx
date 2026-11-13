"use client";

import { useEffect, useState } from "react";

/** A full id with a one-click copy button; ids are long, and operators paste them into tickets and queries. */
export function CopyId({ id }: { id: string }) {
  const [copied, setCopied] = useState(false);

  useEffect(() => {
    if (!copied) return;
    const timer = setTimeout(() => setCopied(false), 1200);
    return () => clearTimeout(timer);
  }, [copied]);

  return (
    <span className="copy-id">
      <code>{id}</code>
      <button
        type="button"
        className="copy"
        aria-label={copied ? "Copied" : `Copy ${id}`}
        data-copied={copied || undefined}
        onClick={() => navigator.clipboard?.writeText(id).then(() => setCopied(true), () => undefined)}
      >
        {copied ? "Copied" : "Copy"}
      </button>
    </span>
  );
}
