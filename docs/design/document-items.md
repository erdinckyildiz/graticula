# Document items — what the screen needs (ADR-188)

**Built 2026-10-28** (ADR-188 condition 4): Content's rows, *New item › Document* and the item page at
`#/item/<id>`. What the review left for later is [D-321](../architecture-debt.md).

For whoever draws or builds Studio's document screens: the redesign in code, or Stitch if the owner wants a draft
first (*"arayüzünü stitch tasarlayabilir çok lazımsa"*, 2026-10-16). The API is built and tested; nothing here is
a decision the screen may change — it is what the screen has to show and send.

## Where it lives

- **My content (ST-01)** lists documents beside services and web maps, from `GET /content/documents`, in the same
  table, folders and scopes. A document row's icon says its kind (PDF, Word, Excel, PowerPoint, CSV, picture), and
  its type column reads the ArcGIS type the API returns as `type`.
- **New item (ST-02)** gains a choice, **Upload a document**: a file picker, a title (the file's name without its
  extension until the person types one), an optional summary, and the sharing level. The picker says what is
  accepted — `accepted` and `maximumBytes` come back with the list — and is not a promise: the server decides.
- **Document item (ST-31)** at `#/document/<id>`, shaped like the web map item (ST-19): title, summary,
  description, tags, owner, sharing, folder, delete protection, the file's name, kind and size, and two actions —
  **Download** and, for its manager, **Replace file**.

## What the API answers and refuses, in words the screen should show as given

| Action | Call | A refusal the screen must show |
|---|---|---|
| list | `GET /content/documents` | 401 when signed out |
| upload | `POST /content/documents`, multipart `file`, `title`, `snippet`, `sharing` | **415** not an accepted kind, or not what its name says (the sentence names the real kind); **413** over `Graticula:DocumentMaxMB`; 403 when the sharing level needs a privilege the person lacks |
| read | `GET /content/documents/{id}` | 404 for one that is not there **or not shared with them** — the same answer by design |
| download | `GET /content/documents/{id}/data` | always a download; never open it in a frame or a new tab as a page |
| change | `PUT /content/documents/{id}` `{title, snippet, sharing}` | 403 for a reader who is not the owner |
| replace | `PUT /content/documents/{id}/data`, multipart `file` | as upload |
| description, tags | `PUT …/description` `{description}`, `PUT …/tags` `{tags}` | 400 with the reason |
| protection | `PUT …/protection` `{protected}` | — |
| delete | `DELETE /content/documents/{id}` | **409** while protected |
| owner | `PUT …/owner` `{to}` | administrators only |
| group | `PUT` / `DELETE /admin/groups/{name}/documents/{id}` | the group's own refusals |
| folder | `POST /content/move` `{to, items: [{document: id}]}` | per item |

## States

loading · none yet · uploading (a determinate bar — the file can be 64 MB) · refused (the sentence, the file
kept in the picker so it can be renamed and sent again) · reader (no Replace, no Delete, no Settings) ·
protected (Delete disabled, with the reason) · download.

## Two traps every new screen here has had

The memory notes call them out because they recurred: a redraw must not drop focus, and an asynchronous result
(uploaded, refused) is announced in a live region rather than painted silently.
