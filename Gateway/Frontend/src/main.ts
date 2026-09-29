import './styles/app.scss';

// FontAwesome: the CSS ends up in the extracted stylesheet, the referenced
// font files become hashed assets below /assets/.
import '@fortawesome/fontawesome-free/css/fontawesome.css';
import '@fortawesome/fontawesome-free/css/solid.css';

import { nodeMenu, startNode } from '@node/start';

import { configurationPage } from './pages/configuration';
import { dnsPage }           from './pages/dns';
import { ntsPage }           from './pages/nts';
import { certificatesPage }  from './pages/certificates';

// What a gateway has pages for is what every node has: its configuration,
// name resolution, the time, the certificate store and the log. The sign-in,
// the log, the frame and following the log while somebody is signed in are
// every node's - see WWCP_Node's start.ts. The OCPP forwarding will bring the
// first page of a gateway's own.
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

    pages: {

        // "/" is the configuration, and is a page of its own rather than a
        // redirect to /configuration: the sign-in remembers where somebody was
        // going, and for the first visit that is "/".
        '/':                            configurationPage,
        '/configuration':               configurationPage,
        '/configuration/dns':           dnsPage,
        '/configuration/nts':           ntsPage,
        '/configuration/certificates':  certificatesPage

    }

});
