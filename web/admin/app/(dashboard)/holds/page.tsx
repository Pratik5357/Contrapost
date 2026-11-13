import { Badge, Empty, Id, Pager, ResultSummary, Time } from "@/components/ui";
import { adminList } from "@/lib/api";
import { money } from "@/lib/format";
import type { AdminHold, Page } from "@/lib/types";

export const metadata = { title: "Holds" };

type Params = { status?: string; expiring?: string; after?: string };

const statuses = ["pending", "partially_captured", "captured", "voided", "expired"];

export default async function HoldsPage({ searchParams }: { searchParams: Promise<Params> }) {
  const params = await searchParams;
  const { data: page, invalid } = await adminList<Page<AdminHold>>("/holds", {
    status: params.status,
    expiring_within: params.expiring ? 600 : undefined,
    after: params.after,
  });

  return (
    <>
      <h1>Holds</h1>
      <p className="subtitle">Money reserved but not yet posted (design doc §4.4).</p>

      <form className="filters" method="get">
        <label>
          Status
          <select name="status" defaultValue={params.status ?? ""}>
            <option value="">any</option>
            {statuses.map((s) => (
              <option key={s} value={s}>
                {s.replaceAll("_", " ")}
              </option>
            ))}
          </select>
        </label>
        <label className="inline">
          <input type="checkbox" name="expiring" value="1" defaultChecked={Boolean(params.expiring)} />
          Expiring in the next 10 minutes
        </label>
        <button type="submit">Filter</button>
      </form>

      {page && (
        <ResultSummary
          count={page.items.length}
          more={page.next_cursor !== null}
          filtered={Boolean(params.status || params.expiring)}
          clearHref="/holds"
        />
      )}
      {invalid !== null ? (
        <p className="error" role="alert">
          {invalid}
        </p>
      ) : page.items.length === 0 ? (
        <Empty>No holds match.</Empty>
      ) : (
        <div className="table-wrap">
          <table>
            <thead>
              <tr>
                <th>Hold</th>
                <th>Client</th>
                <th>Status</th>
                <th>Debit</th>
                <th>Credit</th>
                <th className="num">Amount</th>
                <th className="num">Captured</th>
                <th className="num">Remaining</th>
                <th>Expires</th>
              </tr>
            </thead>
            <tbody>
              {page.items.map(({ client_id, hold }) => (
                <tr key={hold.id}>
                  <td>
                    <Id id={hold.id} />
                  </td>
                  <td>
                    <Id href={`/accounts?client_id=${client_id}`} id={client_id} />
                  </td>
                  <td>
                    <Badge value={hold.status} />
                  </td>
                  <td>
                    <Id href={`/accounts/${hold.debit_account_id}`} id={hold.debit_account_id} />
                  </td>
                  <td>
                    <Id href={`/accounts/${hold.credit_account_id}`} id={hold.credit_account_id} />
                  </td>
                  <td className="num">{money(hold.amount, hold.currency)}</td>
                  <td className="num">{money(hold.captured_amount, hold.currency)}</td>
                  <td className="num">{money(hold.remaining, hold.currency)}</td>
                  <td><Time iso={hold.expires_at} /></td>
                </tr>
              ))}
            </tbody>
          </table>
        </div>
      )}
      <Pager basePath="/holds" params={params} next={page?.next_cursor ?? null} />
    </>
  );
}
