import './styles/app.scss';

// FontAwesome: the CSS ends up in the extracted stylesheet, the referenced
// font files become hashed assets below /assets/.
import '@fortawesome/fontawesome-free/css/fontawesome.css';
import '@fortawesome/fontawesome-free/css/solid.css';

import { html } from '@node/html';
import { nodeMenu, startNode } from '@node/start';

import { configurationPage } from './pages/configuration';

// What a gateway has pages for is what every node has: its configuration,
// name resolution, the time, the certificate store and the log. The sign-in,
// the log, the name servers, the time servers, the certificate store, the
// frame and following the log while somebody is signed in are every node's -
// see WWCP_Node's start.ts. The OCPP forwarding will bring the first page of a
// gateway's own.
startNode({

    name:  'Gateway',
    icon:  'fa-network-wired',

    menu: [
        nodeMenu.configuration([
            nodeMenu.dns,
            nodeMenu.nts,
            nodeMenu.certificates
        ]),
        nodeMenu.logs
    ],

    // The certificate store says what every node says of it, in a gateway's
    // name - but a gateway keeps two kinds nothing here uses yet, a client
    // root and its identity, and says so under what it believes and what it
    // presents.
    certificates: {
        hints: {
            believes:  html`
                Trust anchors, beside the roots of this machine. Every switched-on TLS root is believed at
                once, for the servers it is kept for - a time server or a name server with a CA of its own
                is reached through one. A client root is what a client connecting to this gateway will have
                to chain to; nothing here asks a client for a certificate yet.
            `,
            presents:  html`
                Its identity in TLS, with its private key. Kept for when this gateway speaks TLS itself;
                nothing here presents it yet.
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
