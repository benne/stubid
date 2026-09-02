# What the screens showed during a transaction signing

Observed on 2026-09-02, in the sitting that recorded CAP-031, against `pp.netseidbroker.dk`
with MitID's pre-production test tool. The recordings settle what the tokens carry; nothing in
a recording says what was on screen, because a callback fixture is a redirect query string.

The evidence is three screenshots, which are not in this repository and will not be: no
screenshot of a MitID screen enters the tree, by the rule in
[the runbook](../capture-session.md). The two sections that follow describe them. The third is
documentary, and says where it is reading rather than looking.

Scope: one sitting, one test identity, the code-app simulator reached by QR rather than a
MitID app on a phone. A single observation of a screen is weaker evidence than a recorded
byte.

## The transaction text was on the broker's page, beside MitID rather than inside it

The authorize page put two panels side by side. The left one was styled as a paper receipt,
perforated along the bottom edge, and held `StubID transaction text one` and nothing else on
the panel — the decoded transaction text CAP-031 sent, as plain text. The right one was the
MitID widget: the MitID logo, the heading `Godkend hos <service provider>`, `Scan QR-kode med
MitID app`, a QR code, and `Afbryd` / `Hjælp` beneath it.

So the broker renders it — its page, its panel, beside a widget that says nothing about the
text.

## What MitID held for the same transaction

The test tool's transaction page carried a collapsed `Flow Value Texts` field. Expanded, it
held three entries and no others:

| Field | Value |
| --- | --- |
| Service Provider | the relying party's registered display name |
| Reference Text | `Godkend` |
| Reference Text Header | `Godkend hos <service provider>` |

The transaction text was not among them in any form — not the text, not the base64 it was sent
as, not the digest. The operator's note on the app simulator was "I don't even know if it
really says it anywhere normally", which is the right amount of confidence to take from one
screen.

CAP-031 sent neither `reference_text` nor `action_text`, so `Godkend` and its header came from
somewhere other than the request; whether MitID supplied them or the broker did is not
something one screen can tell apart. MitID's UX scheme composes that header out of an action
text and the service provider's name, and `Godkend hos <service provider>` is that shape.

The same page gave the push title as `Godkend med MitID` and the body as `Godkend transaktion
med MitID`, with `Language: da` and `Channel binding: QR`. The body names a transaction where
a login would not, which is not evidence but a hint: no sitting recorded this screen for a
plain login, so there is nothing to compare it against.

## The split is documented: `reference_text` is MitID's, `transaction_text` is the broker's

The two parameters have different jobs, and the broker's documentation said so until the rows
saying it were removed. What follows is quoted from
[the broker's documentation repository](https://github.com/Signaturgruppen-A-S/signaturgruppen-broker-documentation),
by commit, because most of it is no longer served.

`reference_text` is MitID's, and this half is still published. The MitID identity-provider
page documents it as base64, capped at 130 characters, and "displayed to the user in all MitID
flows inside the MitID client, but only at the last authenticator shown" — and, in the same
table, "shown to the user in the MitID App".

`transaction_text` is the broker's. A prose section removed in June 2025 (`f223bb2`) drew the
line: MitID "natively supports the `reference_text` (130 characters) parameter", where
transaction signing is something "Signaturgruppen Broker supports … as part of the MitID
authentication", limited to signed requests. The rows documenting `transaction_text` and
`transaction_text_type` went days earlier (`a354e83`); they stand at `10c922b`, and while they
stood they gave the base64 encoding and the `text` / `html` type set that CAP-031 used, and
said the text "is presented to the end-user as part of the MitID flow" — a sentence that names
no screen.

That same removed section has the broker parsing `html` transaction texts against a tag
allowlist "to protect against possible malicious content and flow breakage", and says "the
end-user will be shown the text/HTML and will have to approve the text to complete the
transaction". Handling the markup is a step towards rendering it rather than proof of it. The
stronger corroboration is pictorial: the documentation once carried three screenshots of this
screen, removed in August 2024 (`77f2877`) but still served under
[`/images/looknfeel1.png`](https://signaturgruppen-a-s.github.io/signaturgruppen-broker-documentation/images/looknfeel1.png)
and its two siblings. They show a receipt-styled panel on the left holding the plain text and
the MitID widget on the right, headed `Godkend hos` a demo service provider. That is the
layout observed here, published by the vendor two years earlier.

Those screenshots illustrate the sibling `signtext_id` route rather than the `idp_params`
route CAP-031 took, and no document says the two render alike. The observation is what closes
that gap: the broker no longer documents how to send this parameter at all.

## What this does not settle

**Whether a supplied `reference_text` replaces `Godkend`.** It should — that is what the
parameter is for — but the two halves have never met. CAP-022 sent a reference text and
recorded no screens; CAP-031 recorded screens and sent no reference text. The two are not even
the same kind of flow: CAP-022's `transaction_actions` is the bare string `"mitid.login"`,
CAP-031's is `["mitid.login", "mitid.transaction_signing"]`.

**What a MitID app on a phone draws.** None was used, and the flow values are what MitID held
for the transaction rather than a rendering of them. A test tool that lists them in a table is
not obliged to show them the way an app does, and nothing here reaches the app at all.

Each costs an authentication, and neither is worth a sitting on its own. A sitting that sends
a `reference_text` alongside a `transaction_text` settles the first for free, which is the
step worth booking.
