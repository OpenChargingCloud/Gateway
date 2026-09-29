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
// of its own: its configuration's own section and the kinds its store keeps.
// It has no routes of its own yet; the OCPP forwarding will bring the first.
export * from '@node/api/client';


/**
 * What a role may be allowed to touch on this gateway: what every node has,
 * for a gateway adds nothing of its own yet.
 */
export type Resource = NodeResource;

/** What somebody signed in to this gateway may do: an operation on a resource, written "dns:edit". */
export type Permission = `${Resource}:${Operation}`;

/** Who is signed in to the web interface. */
export type Me = NodeMe<Resource>;

/** How the gateway is doing right now: what every node says, and nothing more yet. */
export type Status = NodeStatus;

/**
 * What the gateway is made of: every node's sections, and its own. Only the
 * shape the Configuration page relies on is named; the rest is rendered from
 * whatever the gateway sends, so that a new section on the server needs no
 * change here.
 */
export interface Configuration extends NodeConfiguration {
    gateway:     Record<string, unknown>;
    assemblies:  Record<string, unknown>[];
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
