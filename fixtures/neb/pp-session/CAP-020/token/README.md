# Incomplete: the response body was lost

The successful token exchange was recorded here, then the code-replay probe that follows it
wrote to the same directory and overwrote the response. Only the decoded token sidecars
survived, because they are separate files the error response did not produce.

What is here is genuine: these are the tokens from the successful exchange. What is missing
is the response envelope they arrived in.

The replay itself is intact, in `../token-replay`. An equivalent successful exchange, with the
same client and the same scope, is in `../../CAP-024/token`, which is the one to compare
against.

The harness now numbers repeated exchanges within a step, so this cannot recur.
