namespace UniversityOS.Infrastructure

open System
open UniversityOS.Domain

/// FTS5 maintenance and querying. Repositories call `upsert`/`remove` on every
/// write so global search never goes stale.
[<RequireQualifiedAccess>]
module Fts =

    let upsert (db: Database) (kind: EntityKind) (id: Guid) (title: string) (body: string) : Async<unit> =
        async {
            let kindStr = EntityKind.toString kind
            let idStr = id.ToString "N"

            do! db.ExecuteTransaction(
                    [ "DELETE FROM search_index WHERE entity_kind = $kind AND entity_id = $id",
                      [ "$kind", box kindStr; "$id", box idStr ]

                      "INSERT INTO search_index(entity_kind, entity_id, title, body) VALUES ($kind, $id, $title, $body)",
                      [ "$kind", box kindStr
                        "$id", box idStr
                        "$title", box title
                        // FTS content is capped so enormous documents don't bloat the index.
                        "$body", box (if body.Length > 100_000 then body.Substring(0, 100_000) else body) ] ]
                )
        }

    let remove (db: Database) (kind: EntityKind) (id: Guid) : Async<unit> =
        db.Execute(
            "DELETE FROM search_index WHERE entity_kind = $kind AND entity_id = $id",
            [ "$kind", box (EntityKind.toString kind); "$id", box (id.ToString "N") ]
        )

    /// Escapes user input into an FTS5 prefix query: each term quoted, last term
    /// gets a * so search-as-you-type works.
    let private toMatchQuery (input: string) =
        let terms =
            input.Split([| ' '; '\t'; '\n' |], StringSplitOptions.RemoveEmptyEntries)
            |> Array.map (fun t -> t.Replace("\"", ""))
            |> Array.filter (fun t -> t.Length > 0)

        match terms with
        | [||] -> None
        | _ ->
            terms
            |> Array.mapi (fun i t ->
                if i = terms.Length - 1 then sprintf "\"%s\"*" t else sprintf "\"%s\"" t)
            |> String.concat " "
            |> Some

    let query (db: Database) (text: string) (limit: int) : Async<SearchHit list> =
        async {
            match toMatchQuery text with
            | None -> return []
            | Some matchQuery ->
                try
                    return!
                        db.Query(
                            """
                            SELECT entity_kind, entity_id,
                                   highlight(search_index, 2, '', '') AS h_title,
                                   snippet(search_index, 3, '', '', ' … ', 12) AS snip,
                                   bm25(search_index) AS score
                            FROM search_index
                            WHERE search_index MATCH $q
                            ORDER BY bm25(search_index)
                            LIMIT $limit
                            """,
                            [ "$q", box matchQuery; "$limit", box (int64 limit) ],
                            fun r ->
                                { Kind =
                                    Sql.str r "entity_kind"
                                    |> EntityKind.parse
                                    |> Option.defaultValue EntityKind.Note
                                  Id = Guid.Parse(Sql.str r "entity_id")
                                  Title = Sql.str r "h_title"
                                  Snippet = Sql.str r "snip"
                                  // bm25 is negative (better = more negative); flip for intuitive scores.
                                  Score = -(Sql.float' r "score") }
                        )
                with _ ->
                    // A malformed MATCH expression should never crash search.
                    return []
        }
