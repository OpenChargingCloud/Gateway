import './styles/app.scss';

// FontAwesome: the CSS ends up in the extracted stylesheet, the referenced
// font files become hashed assets below /assets/.
import '@fortawesome/fontawesome-free/css/fontawesome.css';
import '@fortawesome/fontawesome-free/css/solid.css';

import { toURL } from '@node/basePath';
import { requestTabId } from '@node/certificateRequests';
import { html } from '@node/view';
import { nodeMenu, startNode } from '@node/start';

import { sshKeysSection } from '@ocpp/sshKeys';
import { connectionsPage } from '@ocpp/pages/connections';
import { setNodeNoun, setNodeTraits } from '@ocpp/settings';

import { configurationPage } from './pages/configuration';

// What a gateway dials are connections, drawn by the pages a charging station
// and a local controller draw theirs with: where each goes, what it signs in
// with - on a tab of the same page - and how it knows its server. Whoever may
// read or change "connections" may read or change them, as on a station. The
// pages speak of a gateway, which is one OCPP node whatever version it speaks;
// it is not dialled itself yet - the server that lets charging stations, local
// controllers and a CSMS in comes later.
setNodeNoun('gateway');
setNodeTraits({ oneNodePerOCPPVersion: false, isDialled: false });

// What a gateway has pages for beside what every node has: its connections.
// The sign-in, the log, the name servers, the time servers, the SSH server,
// the certificate store, the client and server identities, the frame and
// following the log while somebody is signed in are every node's - see
// WWCP_Node's start.ts.
startNode({
    name:  'Gateway',
    icon:  'fa-network-wired',
    menu: [
        nodeMenu.configuration([
            nodeMenu.dns,
            nodeMenu.nts,
            nodeMenu.ssh,
            // Where the gateway dials, and on a tab of the same page what it
            // signs in with. The keys it holds up have no page: they are TLS
            // identities of the certificate store, made, waiting for their
            // certificate and kept on Client identities - where the SSH keys
            // are as well.
            { path: '/configuration/connections', label: 'Connections', icon: 'fa-network-wired', permission: [ 'connections:read' ] },
            nodeMenu.certificates,
            nodeMenu.identities,
            // What the web interface shows over HTTPS, where the "web"
            // section says "tls": { "certificate": "store" } or --https.
            nodeMenu.serverIdentities
        ]),
        nodeMenu.logs
    ],
    // The certificate store says what every node says of it, in a gateway's
    // name - and where a gateway uses what it keeps: a TLS root for the time
    // servers, the name servers and the servers of its connections, a TLS
    // identity for a CSMS its connections show it to. A client root is kept
    // for the server the charging stations will connect to, which is not here
    // yet. What its web interface shows over HTTPS is on the Server
    // identities page.
    certificates: {
        hints: {
            believes:  html`
                Trust anchors, beside the roots of this machine. Every switched-on TLS root is believed at
                once, for the servers it is kept for - a time server, a name server or the CSMS of a
                connection with a CA of its own is reached through one. A client root is what a client
                connecting to this gateway will have to chain to; nothing here asks a client for a
                certificate yet.
            `,
            presents:  html`
                A TLS identity, with its private key: what this gateway shows a CSMS it dials that asks who
                it is - the <a href="${toURL('/configuration/connections')}">Connections</a> page says which
                one each connection shows. An identity is brought here with its key, or its key is
                <a href="${toURL(`/configuration/client-identities?tab=${requestTabId}`)}">made on this gateway</a>
                and only its certificate ever travels; a key still waiting for its certificate is below.
            `,
            serves:    html`
                What the web interface, the JSON API and the sign-in show over HTTPS, with its private key -
                where the "web" section of the configuration file says "tls": { "certificate": "store" },
                or the gateway is started with --https.
            `
        },
        // And who it is where it dials over ssh://: the keys it signs in
        // with, which are no certificates and stand among what it presents
        // all the same.
        sections: context => [ sshKeysSection(context) ]
    },
    // "/" is every node's: the first page of the menu the person signed in may
    // open - the configuration for whoever may read it, and the time servers,
    // say, for an account that may read only those, where the configuration's
    // own page had answered 403.
    pages: {
        '/configuration':              configurationPage,
        '/configuration/connections':  connectionsPage
    }
});
