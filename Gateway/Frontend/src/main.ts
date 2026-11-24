import './styles/app.scss';

// FontAwesome: the CSS ends up in the extracted stylesheet, the referenced
// font files become hashed assets below /assets/.
import '@fortawesome/fontawesome-free/css/fontawesome.css';
import '@fortawesome/fontawesome-free/css/solid.css';

import { toURL } from '@node/basePath';
import { requestTabId } from '@node/certificateRequests';
import { html } from '@node/view';
import { nodeMenu, startNode } from '@node/start';

import { sshKeysSection }         from '@ocpp/sshKeys';
import { connectionsPage }        from '@ocpp/pages/connections';
import { ocppLogMenuEntry, ocppLogPage } from '@ocpp/pages/ocppLog';
import { ocppServerPage }         from '@ocpp/pages/ocppServer';
import { stationLoginsPage }      from '@ocpp/pages/stationLogins';
import { clientTrustPage }        from '@ocpp/pages/clientTrust';
import { commissioningPage }      from '@ocpp/pages/commissioning';
import { setNodeNoun, setNodeTraits } from '@ocpp/settings';

import { configurationPage } from './pages/configuration';

// What a gateway connects to are connections, drawn by the pages a charging station
// and a local controller draw theirs with: where each goes, what it signs in
// with - on a tab of the same page - and how it knows its server. Whoever may
// read or change "connections" may read or change them, as on a station. The
// pages speak of a gateway, which is one OCPP node whatever version it speaks,
// and is itself connected to, by the charging stations and local controllers below
// it - and by a CSMS it cannot connect to - on its station server.
setNodeNoun('gateway');
setNodeTraits({ oneNodePerOCPPVersion: false, isConnectedTo: true });

// What a gateway has pages for beside what every node has: its connections,
// and the server the stations below it connect to - with the stations that
// may sign in and their groups, and the chains it accepts - drawn by the
// pages a local controller draws its own with. What the server presents is
// the certificate store's: TLS server identities for the listener "ocpp", and
// its server keys, on the Server identities page every node has.
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
            // Where the gateway connects, and on a tab of the same page what it
            // signs in with. The keys it holds up have no page: they are TLS
            // identities of the certificate store, made, waiting for their
            // certificate and kept on Client identities - where the SSH keys
            // are as well.
            { path: '/configuration/connections', label: 'Connections', icon: 'fa-network-wired', permission: [ 'connections:read' ] },
            nodeMenu.certificates,
            nodeMenu.identities,
            // What the web interface shows over HTTPS, where the "web"
            // section says "tls": { "certificate": "store" } or --https -
            // and what the station server shows, with its server keys.
            nodeMenu.serverIdentities,
            // The station server, as a local controller has it: the same
            // pages, paths, labels and rights.
            { path: '/configuration/ocpp-server',              label: 'Charging stations',   icon: 'fa-charging-station',  permission: [ 'stations:read' ]     },
            { path: '/configuration/ocpp-server/logins',        label: 'WebSocket logins',    icon: 'fa-users-gear',        permission: [ 'stations:read' ]     },
            { path: '/configuration/ocpp-server/trust',         label: 'Accepted chains',     icon: 'fa-user-shield',       permission: [ 'certificates:read' ] },
            { path: '/configuration/commissioning',             label: 'Commissioning',       icon: 'fa-screwdriver-wrench', permission: [ 'commissioning:read' ] }
        ]),
        // Every OCPP message this node sent and received.
        ocppLogMenuEntry,
        nodeMenu.logs
    ],
    // The certificate store says what every node says of it, in a gateway's
    // name - and where a gateway uses what it keeps: a TLS root for the time
    // servers, the name servers and the servers of its connections, a TLS
    // identity for a CSMS its connections show it to. A client root is kept,
    // and the station server judges a station's certificate by its own
    // Accepted chains instead. What its web interface shows over HTTPS, and
    // what its station server shows - with the server keys the stations pin -
    // are on the Server identities page.
    certificates: {
        hints: {
            believes:  html`
                Trust anchors, beside the roots of this machine. Every switched-on TLS root is believed at
                once, for the servers it is kept for - a time server, a name server or the CSMS of a
                connection with a CA of its own is reached through one. A client root is kept and used by
                nothing here yet: what the certificate of a charging station below this gateway has to
                lead to is said under <a href="${toURL('/configuration/ocpp-server/trust')}">Accepted chains</a>.
            `,
            presents:  html`
                A TLS identity, with its private key: what this gateway shows a CSMS it connects to that asks who
                it is - the <a href="${toURL('/configuration/connections')}">Connections</a> page says which
                one each connection shows. An identity is brought here with its key, or its key is
                <a href="${toURL(`/configuration/client-identities?tab=${requestTabId}`)}">made on this gateway</a>
                and only its certificate ever travels; a key still waiting for its certificate is below.
            `,
            serves:    html`
                What the web interface, the JSON API and the sign-in show over HTTPS, with its private key -
                where the "web" section of the configuration file says "tls": { "certificate": "store" },
                or the gateway is started with --https - shown on the listener "web"; and what the
                <a href="${toURL('/configuration/ocpp-server')}">station server</a> shows the charging stations
                and local controllers below this gateway, on the listener "ocpp": a certificate an authority
                issued, or a server key of its own that the stations pin. An identity told no listener is
                shown on both.
            `
        },
        // The station server's server keys: offered here before it has one.
        serverKeyListeners: [ 'ocpp' ],
        // And who it is where it connects over ssh://: the keys it signs in
        // with, which are no certificates and stand among what it presents
        // all the same.
        sections: context => [ sshKeysSection(context) ]
    },
    // "/" is every node's: the first page of the menu the person signed in may
    // open - the configuration for whoever may read it, and the time servers,
    // say, for an account that may read only those, where the configuration's
    // own page had answered 403.
    pages: {

        '/ocpp-log':                                ocppLogPage,
        '/configuration':                           configurationPage,
        '/configuration/connections':               connectionsPage,
        '/configuration/ocpp-server':               ocppServerPage,
        '/configuration/ocpp-server/logins':        stationLoginsPage,
        '/configuration/ocpp-server/trust':         clientTrustPage,
        '/configuration/commissioning':             commissioningPage
    }
});
