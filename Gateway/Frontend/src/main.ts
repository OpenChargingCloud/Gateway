import './styles/app.scss';

// FontAwesome: the CSS ends up in the extracted stylesheet, the referenced
// font files become hashed assets below /assets/.
import '@fortawesome/fontawesome-free/css/fontawesome.css';
import '@fortawesome/fontawesome-free/css/solid.css';

import { html } from '@node/view';
import { nodeMenu, startNode } from '@node/start';

import { configurationPage } from './pages/configuration';

// What a gateway has pages for is what every node has: its configuration,
// name resolution, the time, the SSH server, the certificate store, who it is
// as a client, what its web interface shows over HTTPS and the log. The
// sign-in, the log, the name servers, the time servers, the SSH server, the
// certificates, the identities, the frame and following the log while somebody
// is signed in are every node's -
// see WWCP_Node's start.ts. The OCPP forwarding will bring the first page of a
// gateway's own.
startNode({

    name:  'Gateway',
    icon:  'fa-network-wired',

    menu: [
        nodeMenu.configuration([
            nodeMenu.dns,
            nodeMenu.nts,
            nodeMenu.ssh,
            nodeMenu.certificates,
            nodeMenu.identities,
            // What the web interface shows over HTTPS, where the "web"
            // section says "tls": { "certificate": "store" } or --https.
            nodeMenu.serverIdentities
        ]),
        nodeMenu.logs
    ],

    // The certificate store says what every node says of it, in a gateway's
    // name - but a gateway keeps two kinds nothing here uses yet, a client
    // root and its identity as a client, and says so under what it believes,
    // on the Certificates page, and under who it is, on the Identities page.
    // What its web interface shows over HTTPS is on the Server identities page.
    certificates: {
        hints: {
            believes:  html`
                Trust anchors, beside the roots of this machine. Every switched-on TLS root is believed at
                once, for the servers it is kept for - a time server or a name server with a CA of its own
                is reached through one. A client root is what a client connecting to this gateway will have
                to chain to; nothing here asks a client for a certificate yet.
            `,
            presents:  html`
                What it will show a server that asks who it is, with its private key. Kept for when this
                gateway connects to one that does; nothing here presents it yet.
            `,
            serves:    html`
                What the web interface, the JSON API and the sign-in show over HTTPS, with its private key -
                where the "web" section of the configuration file says "tls": { "certificate": "store" },
                or the gateway is started with --https.
            `
        }
    },

    // "/" is every node's: the first page of the menu the person signed in may
    // open - the configuration for whoever may read it, and the time servers,
    // say, for an account that may read only those, where the configuration's
    // own page had answered 403.
    pages: {

        '/configuration':  configurationPage

    }

});
