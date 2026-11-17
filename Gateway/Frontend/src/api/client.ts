import { nodeAPI,
         type Certificate        as NodeCertificate,
         type CertificateImport  as NodeCertificateImport,
         type CertificateStore   as NodeCertificateStore,
         type NodeConfiguration,
         type NodeMe,
         type NodeResource,
         type NodeStatus,
         type Operation }  from '@node/api/client';


// What every node answers - the log, name resolution, the time, the store,
// who is signed in - and how it is asked are WWCP_Node's, and every page here
// reads them from this module as before. What follows is what a gateway says
// of its own: its resource, its configuration's own sections and the kinds its
// store keeps. Its connections are asked as every node that dials asks for
// them - WWCP_OCPP_Node's connectionsAPI, used by the pages of "@ocpp/...".
export * from '@node/api/client';


/**
 * What a role may be allowed to touch on this gateway: what every node has,
 * its connections - where it dials, what it proves itself with there and
 * where each stands - under the name a charging station gives them, and the
 * stations below it - its station server and who may sign in to it - under
 * the name a local controller gives them.
 */
export type Resource = NodeResource | 'connections' | 'stations';

/** What somebody signed in to this gateway may do: an operation on a resource, written "dns:edit". */
export type Permission = `${Resource}:${Operation}`;

/** Who is signed in to the web interface. */
export type Me = NodeMe<Resource>;

/** How the gateway is doing right now: what every node says, and nothing more yet. */
export type Status = NodeStatus;

/**
 * What the gateway is made of: every node's sections, and its own. Only the
 * shape the Configuration page relies on is named; the fields of each
 * section are rendered from whatever the gateway sends, and which sections
 * there are is the page's to say.
 */
export interface Configuration extends NodeConfiguration {
    gateway:        Record<string, unknown>;
    ocpp:           Record<string, unknown>;
    stationServer:  Record<string, unknown>;
}

/**
 * What a certificate in this gateway's store is for. Roots are believed and
 * the gateway's own certificate is presented; a server certificate is
 * neither, but kept to recognise a server by its fingerprint.
 *
 * The four kinds of TLS, which are all a gateway keeps - none of the seven of
 * ISO 15118, which are a vehicle's. The store says the same, and a page shows
 * what the store says.
 */
export type CertificateKind = 'tlsRoot' | 'clientRoot' | 'tlsServer' | 'tlsIdentity';

/** One certificate in the store. */
export type Certificate = NodeCertificate<CertificateKind>;

/** What an import sends. */
export type CertificateImport = NodeCertificateImport<CertificateKind>;

/** The whole store, grouped the way it is shown: every node's, with nothing of a gateway's own. */
export type CertificateStore = NodeCertificateStore<CertificateKind>;


/** How the gateway is asked: the routes every node has, typed with the gateway's own of them. */
export const api = {
    ...nodeAPI<{ me: Me; status: Status; configuration: Configuration; kind: CertificateKind; store: CertificateStore }>()
};
