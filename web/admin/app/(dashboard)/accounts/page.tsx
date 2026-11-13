import Link from "next/link";
import { Badge, Empty, Pager, ResultSummary } from "@/components/ui";
import { adminList } from "@/lib/api";
import { money, short } from "@/lib/format";
import type { AdminAccount, Page } from "@/lib/types";

export const metadata = { title: "Accounts" };

type Params = { q?: string; client_id?: string; currency?: string; status?: string; after?: string };

export default async function AccountsPage({ searchParams }: { searchParams: Promise<Params> }) {
  const params = await searchParams;
  const { data: page, invalid } = await adminList<Page<AdminAccount>>("/accounts", params);

  return (
    <>
      <h1>Accounts</h1>
      <p className="subtitle">Every client&apos;s accounts, oldest first.</p>

      <form className="filters" method="get">
        <label>
          Code contains
          <input name="q" defaultValue={params.q} placeholder="wallet:alice" />
        </label>
        <label>
          Client id
          <input name="client_id" defaultValue={params.client_id} size={36} />
        </label>
        <label>
          Currency
          <input name="currency" defaultValue={params.currency} placeholder="INR" size={5} />
        </label>
        <label>
          Status
          <select name="status" defaultValue={params.status ?? ""}>
            <option value="">any</option>
            <option value="active">active</option>
            <option value="frozen">frozen</option>
            <option value="closed">closed</option>
          </select>
        </label>
        <button type="submit">Search</button>
      </form>

      {page && (
        <ResultSummary
          count={page.items.length}
          more={page.next_cursor !== null}
          filtered={Boolean(params.q || params.client_id || params.currency || params.status)}
          clearHref="/accounts"
        />
      )}
      {invalid !== null ? (
        <p className="error" role="alert">
          {invalid}
        </p>
      ) : page.items.length === 0 ? (
        <Empty>No accounts match these filters. Try a shorter code, or clear the filters.</Empty>
      ) : (
        <div className="table-wrap">
          <table>
            <thead>
              <tr>
                <th>Code</th>
                <th>Client</th>
                <th>Status</th>
                <th>Normal side</th>
                <th className="num">Posted</th>
                <th className="num">Available</th>
              </tr>
            </thead>
            <tbody>
              {page.items.map(({ client_id, account }) => (
                <tr key={account.id}>
                  <td>
                    <Link href={`/accounts/${account.id}`}>{account.code}</Link>
                  </td>
                  <td>
                    <Link href={`/accounts?client_id=${client_id}`}>
                      <code title={client_id}>…{short(client_id)}</code>
                    </Link>
                  </td>
                  <td>
                    <Badge value={account.status} />
                  </td>
                  <td>{account.normal_side}</td>
                  <td className="num">{money(account.balance.posted, account.currency)}</td>
                  <td className="num">{money(account.balance.available, account.currency)}</td>
                </tr>
              ))}
            </tbody>
          </table>
        </div>
      )}
      <Pager basePath="/accounts" params={params} next={page?.next_cursor ?? null} />
    </>
  );
}

