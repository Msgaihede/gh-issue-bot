# Retrieval spike

Measures how well code-context retrieval finds the files an issue is about. Not part of the bot; it reuses
the bot's services and configuration.

**Ground truth** is the repository's own history: every closed issue that a merged pull request claims to
fix (`Fixes #N`, `Closes #N`, `Resolves #N`), and the files that pull request changed — limited to files
still in today's repository map.

**Queries** come in two voices:

- *dev* — the issue title and body as written;
- *user* — GPT-6 Luna rewrites the issue as a short Discord message from a non-technical user (no code
  identifiers, jargon or the issue's own terminology), which then goes through the bot's real classification
  and drafting. This is the vocabulary gap the retrievers have to bridge.

**Retrievers** (code files; docs are scored separately with keyword search):

| Name | What it does |
|---|---|
| `A` | BM25 keyword search over each file's path and map summary |
| `A+` | the same, plus search terms GPT-6 Luna writes for the issue (generic developer vocabulary) |
| `A*` | the same, but the terms are written with the repository's file tree in view |
| `B <model>` | cosine similarity between embeddings of the draft and of each file's path + summary |
| `B* <model>` | the same, with the tree-grounded search terms appended to the query |
| `H` | `A*` and a `B*` fused by rank (reciprocal rank fusion) |

Scores: **hit@K** — share of issues with at least one changed file in the top K; **rec@K** — average share
of each issue's changed files in the top K. With `--jev N`, the best retriever's top 40 is handed to Jev to
pick 4 files, next to Jev picking from the whole map (the bot's current behaviour), for the first N issues.

## Running

The repository must have a complete map (run `./run.ps1 --dry-run owner/repo "..."` first). With the bot's
settings in the environment (as `run.ps1` loads them from `.env`):

```
dotnet run --project tools/RetrievalSpike -- owner/repo [--models m1,m2] [--limit N] [--jev N]
```

Every model output is cached under `%TEMP%/retrieval-spike/<owner_repo>/`, so a re-run only pays for what is
new; delete that folder to start over.
