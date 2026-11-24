# Gateway
OCPP Gateway


## The OCPP log

The *OCPP log* page (`/ocpp-log`) shows every OCPP message this gateway sent and
received, over every connection it has: the ones it opened and the ones that
were opened to it, over a WebSocket or over SSH, in OCPP 2.1 and in OCPP 1.6.
A message it only passes on is there twice, as it went over the wire: in from
the node that sent it, and out to the one it went on to - and the answer the
same way back.

A line is one message: the time, which way it went, the node at the other end,
what kind of message it is (`CALL`, `RESULT`, `ERROR`, `RESULT ERROR`, `SEND`),
the action and the JSON array itself. An answer carries no action of its own,
only the id of its request, so it is shown with the action of the request it
answers - which the gateway remembers until the answer comes, apart for each node
at the other end and each direction, since both ends number their requests and
a node that passes a request on asks under an id of its own. Where it never saw
that request the action is a `?`. The node at the other end is the one the
connection was signed in as, or - for a connection of the gateway's own that names
none - the description it was written down with. A click on a line unfolds the
array laid out, with a button that copies it as it went over the wire. The page
follows along live, and filters by what a message says, by direction, by node
and by errors alone, over what is already in the browser.

A message that went over the wire as CBOR - over `ocpp2.1cbor` or
`ocpp1.6cbor` - is shown as the JSON it stands for, and marked `CBOR`: the CBOR
edge turns what comes in into that JSON before the OCPP adapter takes it, and
what goes out is that JSON before it is turned into CBOR. Not shown is CBOR that
stands for no OCPP message at all, which the edge refuses before anything else
sees it, and a message of the binary format of the BinaryDataStreams extension.
A message that was never sent - no connection to go over, or one it could not
be written to - is not shown either: this is what went over the wire.

It is kept in memory and nowhere else: the last 2000 messages, the oldest
falling out as new ones come. It is not written to a file, a restart starts it
empty, and *Clear view* clears only what the page shows. This is a different
thing from the gateway's event log, whose files keep every entry and whose last
2000 entries are only what the Logs page shows: an OCPP message carries
identification tokens, card numbers and meter readings - who charged, where and
how much - which is why the event log takes them in only for a window somebody
opened (`webSocketServer.logging.payloads`), and why this log is written nowhere.

For the same reason it is for whoever may change the station server -
`stations:edit` - which is what opening that window asks for: the page shows
nobody what they could not have switched into the event log themselves. The
menu offers it to them alone, and the node asks again on every request and,
for the live stream, before every message.

The JSON API has it as the event log is had - a snapshot, then the stream from
where it ends:

- `GET /api/v1/ocpp/messages?limit=&after=&peer=` - the newest `limit` messages
  (500 unless asked, at most 2000), oldest first; only those after the id
  `after`, and only those of one node at the other end with `peer`. With them
  `lastId`, `count`, `capacity` and every node seen in `peers`.
- `GET /api/v1/ocpp/messages/events` - Server-Sent Events, one `message` event
  per message.

A message is
`{ id, timestamp, direction, kind, ocppVersion, peer, messageId, action, message, encoding }`,
`direction` being `in` or `out`, `kind` one of `request`, `response`,
`requestError`, `responseError` and `send`, and `encoding` `json` or `cbor`.


## Your participation

This software is Open Source under the **Affero GPL 3.0 license**.
We appreciate your participation in this ongoing project, and your help to
improve it and the e-mobility ICT in general. If you find bugs, want to
request a feature or send us a pull request, feel free to use the normal
GitHub features to do so. For this please read the Contributor License
Agreement carefully and send us a signed copy or use a similar free and
open license.
