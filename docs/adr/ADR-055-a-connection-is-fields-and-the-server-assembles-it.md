# ADR-055 — A connection is fields, and this server assembles it

| | |
|---|---|
| **Status** | `ACCEPTED` |
| **Confidence** | `HIGH` |
| **Decided** | 2026-09-05, by owner decision |
| **Supersedes** | — |
| **Superseded by** | — |

---

## 1. Context

Registering a data source meant typing an Npgsql connection string into one text box, and
correcting one meant typing the whole thing again. That box is the first thing a new
operator meets and the last thing anybody gets right first time: it is a format with
quoting rules, several accepted spellings for the same keyword, and exactly one failure
mode — the whole string is refused and the message is about whichever part the provider
noticed first.

The owner sent a picture of ArcGIS Pro's **Database Connection** dialog on 2026-09-05 and
asked for it here: a modal with *Instance*, *User Name*, a masked *Password* and a
*Database* combo, opened by both registering and editing. And one sentence describes the
part that is not layout: *user pass girdikten sonra comboya basınca eğer ki her şey
doğruysa* — press the combo after the credential, and if everything is right it fills.

That makes the database list the test. Nothing comes back unless the host resolved, the
port answered, TLS agreed and the credential was accepted, so a filled combo says all four
at the moment somebody was going to type the value anyway — instead of a *Test* button
that says *usable* and leaves them to type a database name they may still spell wrong.

The immediate question is where the connection string gets built.

## 2. Alternatives considered

### Alternative A — The browser assembles the string and the server keeps taking one

**Argument for.** No server change at all. Every endpoint already takes
`connectionString`, the console has the fields on screen, and joining them with semicolons
is four lines of JavaScript.

**Argument against.** It is four lines of JavaScript that reimplement Npgsql's quoting.
A value containing `;` must be wrapped in quotes and an internal `"` doubled — and the
field where those characters actually turn up is the password. So the naive version works
for every password anybody tests with and silently produces a different connection for the
ones that matter. The same objection runs backwards for editing: the console would have to
take the stored string apart to fill the fields.

### Alternative B — A structured connection is stored, and the string stops being the record

**Argument for.** The string is a transport format; storing host, port, database and user
as columns would make them queryable, and *which sources point at this host* would be a
question with an answer.

**Argument against.** It is a migration of a sealed credential column for a benefit
nobody has asked for, and it throws away the one thing a string is good at: carrying the
options this form does not know about. An SSL mode, a timeout, an application name and
whatever Npgsql adds next all have to survive, and a set of columns is a list that has to
be extended every time.

### Alternative C — Keep the single box and add a *Test* button that lists databases into a hint

**Argument for.** The smallest change that answers the owner's actual complaint, which is
that nothing tells you the connection is right until you commit to it.

**Argument against.** It leaves the operator reading names out of a hint and typing them
back into the string, which is the transcription error the whole request is about.

## 3. Counterarguments to the preferred option

**Two ways to say the same thing is two things to maintain.** The fields and the raw
string are both accepted, and a request carrying each of them is refused rather than
resolved — so the ambiguity is a 400 rather than a guess. The fields are the form's path
and the string is the escape hatch; removing the string would make an SSL mode
unreachable from this console, which is the inverse of [ADR-034](ADR-034-server-and-studio.md)'s
rule: a capability with no control.

**Listing databases makes this server a credential oracle.** It does, and it already was.
`POST /admin/datasources/test` has taken an arbitrary host and credential from the same
privilege since [ADR-017](ADR-017-admin-api.md), opened a connection to it,
and reported in detail what happened — including *the password was rejected* as distinct
from *no host by that name*. The listing reaches nothing further and reads strictly less of
what it finds. What would be new is a **weaker** privilege being able to do it, so it is
guarded by `content:registerDataStore` like its neighbours.

**`postgres` is assumed to exist.** A session has to be somewhere before it can ask what
else there is, and PostgreSQL offers no way around that. A server that has dropped its
maintenance database answers `3D000`, and the refusal names the database that was tried —
otherwise *that database does not exist* names one the operator never typed.

## 4. Evidence

| | |
|---|---|
| Fields the dialog asks for | 6 — name, instance, port, user, password, database |
| Controls in the owner's picture that are **not** drawn | 3 |
| Endpoints that now accept either form | 4 — test, register, correct, list |
| Places that assemble a connection string | **1**, `AdminEndpoints.Assemble` |

The three omissions are the design rather than a shortfall. *Database Platform* is a combo
with one entry, because v1 is PostGIS and nothing else. *Authentication Type* is the same.
*Save User/Password* cannot be unticked: this server reads the source long after whoever
registered it went home, so the sealed credential is what makes it a registration rather
than a session. ADR-034 §2: a control is not drawn for a feature that does not exist.

One control is added that the picture folds away. Pro writes the port into *Instance* as
`host,port`; Npgsql takes it separately, and hiding it would put a database on 5433 out of
reach of this form entirely.

## 5. Decision

**A connection may be sent as fields, and this server assembles it.**
`DataSourceRequest` carries `host`, `port`, `database`, `username` and `password` beside
`connectionString`, and `Assemble` turns whichever arrived into one string with
`NpgsqlConnectionStringBuilder` — the same type that takes it apart again in
`WithoutSecrets`. Both forms in one request is a 400.

**`POST /admin/datasources/databases` answers what a server holds.** It connects with
whatever the caller has typed so far, defaulting the database to `postgres`, and returns
the names that are not templates and allow connections. It answers **200 with an outcome**
whether or not the credential worked, because *your password is wrong* is the answer to
the question rather than a failure to answer it.

**`GET /admin/datasources/{id}/connection` returns the fields as well as the string**, so
the correction form fills without the browser parsing anything.

**The console asks for the fields in a modal, and the same modal registers and corrects.**
The password is masked, is never filled in on either path, and is required to save.

## 6. Consequences

- **The database combo is the connection test**, and the *Test connection* button beside it
  is now the second opinion rather than the first. Both stay: the test reports privileges
  and geometry, which a list of names does not.
- **A datalist, not a select.** A database an account may connect to is not always one it
  may see in `pg_database`; a closed list would refuse the correct answer on a server that
  hides them.
- **The raw string is one disclosure away** and overrides the fields when filled. That is
  stated on the control rather than inferred.
- **The old single-box form is gone**, and with it `drawSourceEdit` and `saveSourceEdit`.
  Two forms for one job is how the empty-box defect in
  [D-228](../architecture-debt.md) came to exist in only one of them.
- **State.** Nothing new is stored. This decision changes how a connection string is
  *composed* and adds a read that keeps nothing; the sealed credential column, its key and
  its lifecycle are exactly as [ADR-017](ADR-017-admin-api.md) left them.
- **The listing is not audited**, because it writes nothing and a log of every combo press
  would bury the registrations that matter in the same file.

## 7. Conditions

1. **A second database platform reopens the omissions.** *Database Platform* and
   *Authentication Type* are undrawn because each has one value; the day either has two,
   this dialog owes the control and this ADR owes the reasoning for what the second one
   does to a stored source.
2. **The listing's reach is re-argued if the privilege ever splits.** The security
   argument in §3 rests entirely on `content:registerDataStore` already being able to make
   this server connect anywhere. A narrower privilege that can register but not test —
   or a delegated one — makes that sentence false, and this endpoint is where it would
   first be false.
