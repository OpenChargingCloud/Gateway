import './styles/app.scss';

// FontAwesome: the CSS ends up in the extracted stylesheet, the referenced
// font files become hashed assets below /assets/.
import '@fortawesome/fontawesome-free/css/fontawesome.css';
import '@fortawesome/fontawesome-free/css/solid.css';

import { nodeMenu, startNode } from '@node/start';

import { configurationPage } from './pages/configuration';
import { certificatesPage }  from './pages/certificates';

// What a gateway has pages for is what every node has: its configuration,
// name resolution, the time, the certificate store and the log. The sign-in,
// the log, the name servers, the time servers, the frame and following the
// log while somebody is signed in are every node's - see WWCP_Node's start.ts.
// The OCPP forwarding will bring the first page of a gateway's own.
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

    // "/" is every node's: the first page of the menu the person signed in may
    // open - the configuration for whoever may read it, and the time servers,
    // say, for an account that may read only those, where the configuration's
    // own page had answered 403.
    pages: {

        '/configuration':               configurationPage,
        '/configuration/certificates':  certificatesPage

    }

});
