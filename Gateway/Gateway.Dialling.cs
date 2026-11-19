/*
 * Copyright (c) 2014-2026 GraphDefined GmbH <achim.friedland@graphdefined.com>
 * This file is part of Gateway <https://github.com/OpenChargingCloud/Gateway>
 *
 * Licensed under the Affero GPL license, Version 3.0 (the "License");
 * you may not use this file except in compliance with the License.
 * You may obtain a copy of the License at
 *
 *     http://www.gnu.org/licenses/agpl.html
 *
 * Unless required by applicable law or agreed to in writing, software
 * distributed under the License is distributed on an "AS IS" BASIS,
 * WITHOUT WARRANTIES OR CONDITIONS OF ANY KIND, either express or implied.
 * See the License for the specific language governing permissions and
 * limitations under the License.
 */

#region Usings

using org.GraphDefined.Vanaheimr.Illias;

using cloud.charging.open.protocols.WWCP.NetworkingNode;
using cloud.charging.open.protocols.WWCP.Node.Certificates;

using cloud.charging.open.protocols.WWCP.Node.Connections;
using cloud.charging.open.protocols.WWCP.Node.Logins;

using cloud.charging.open.Gateway.Configuration;

using OCPPv1_6    = cloud.charging.open.protocols.OCPPv1_6;
using OCPPv2_1    = cloud.charging.open.protocols.OCPPv2_1;
using OCPPv2_1_GW = cloud.charging.open.protocols.OCPPv2_1.Gateway;

#endregion

namespace cloud.charging.open.Gateway
{

    /// <summary>
    /// What a gateway says of itself to the dialling it shares with every node
    /// that dials a CSMS: that it is a gateway, that it speaks OCPP 2.1 and no
    /// OCPP 1.6, and where its connections and its SSH key are kept.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Everything else is WWCPNode's, as it is a charging station's
    /// and a local controller's: the connections and what they prove
    /// themselves with, the identities of the certificate store they show,
    /// how a server is believed - by its CA or by its key pins - and how a
    /// connection is dialled, followed, let go of and dialled again, the next
    /// endpoint of a peer where one cannot be reached.
    /// </para>
    /// <para>
    /// What a gateway does not say yet, and a local controller does: a
    /// networking mode, pings and timeouts of its own, and the device model a
    /// CSMS reads and writes the TLS and SSH clients of its lines through.
    /// Until then a line is opened as OCPPDialling opens one.
    /// </para>
    /// </remarks>
    public partial class Gateway
    {

        #region Data

        /// <summary>
        /// The node that speaks OCPP 2.1 for this gateway.
        /// </summary>
        private readonly OCPPv2_1_GW.TestGatewayNode gw01;

        #endregion

        #region Properties

        /// <summary>
        /// The node that speaks OCPP 2.1 for this gateway.
        /// </summary>
        public OCPPv2_1_GW.TestGatewayNode  Node
            => gw01;

        #endregion

        #region (static) Constructor

        /// <summary>
        /// What the connections of this process say of the node they are the
        /// connections of: it is a gateway.
        /// </summary>
        static Gateway()
        {
            NodeWording.Is("gateway");
        }

        #endregion

        #region Node16 / Node21

        /// <summary>
        /// A gateway speaks no OCPP 1.6 to the CSMS it dials.
        /// </summary>
        protected override OCPPv1_6.NetworkingNode.AOCPPNetworkingNode? Node16
            => null;

        /// <summary>
        /// The node that speaks OCPP 2.1 for this gateway.
        /// </summary>
        protected override OCPPv2_1.NetworkingNode.AOCPPNetworkingNode Node21
            => gw01;

        #endregion


        #region (private) BuildConnectionStores()

        /// <summary>
        /// The stores of what this gateway dials - the connections and their
        /// credentials, the key it signs in with over SSH - beside the
        /// configuration file, and how it believes the servers of its
        /// connections by their key pins.
        /// </summary>
        /// <remarks>
        /// The keys and certificates a connection holds up have no store here:
        /// they are TLS identities of the certificate store.
        /// </remarks>
        private void BuildConnectionStores()
        {

            var directory = Path.GetDirectoryName(Path.GetFullPath(ConfigFile.Path)) ?? ".";

            Connections = new ConnectionStore(
                              Path.Combine(directory, ConnectionStore.DefaultDirectoryName),
                              TimeProvider,
                              NodeNoun
                          );

            keyPinning       = NewKeyPinning();
            OCPPDialling.MakeTLSClientSource();

            // Which identities exist is the certificate store's business and
            // changes while this one is alive, so it is asked rather than told
            // once.
            Connections.KnownCertificateIds   = () => Certificates.ByKind(CertificateKind.TLSIdentity).Select(entry => entry.Id);
            Connections.OnNotice             += (level, message) => Log.Log(level, message, "ocpp", "connections");

            // What is written down is what is dialled, from the moment it is
            // written - once this gateway has started; see WWCPNode.Peers.cs.
            Connections.OnConnectionsChanged += () => _ = ApplyConnectionChanges();

            SshKey = new SshClientKey(
                         Path.Combine(Connections.Path, SshClientKey.DirectoryName),
                         $"gateway@{Environment.MachineName}",
                         NodeNoun:  NodeNoun
                     );

            SshKey.OnNotice += (level, message) => Log.Log(level, message, "ocpp", "connections", "ssh", "security");

            // And the keys somebody made beside it, on the page of the client
            // identities: read now, so that a connection that names one is
            // checked against what is there from the first one written down.
            _ = SshKeys;

        }

        #endregion

        #region (private) BuildOCPPNode(Configuration)

        /// <summary>
        /// The OCPP 2.1 node this gateway speaks through.
        /// </summary>
        /// <remarks>
        /// Every HTTP API the node brings of its own is switched off: this
        /// gateway has one HTTP server, on one port, and a second one appearing
        /// on a port nobody chose would be a door into the same process that the
        /// sign-in in front of this one does not guard.
        ///
        /// Nothing is dialled here. Building the node and dialling a CSMS are two
        /// different things, and the second one is not something a constructor
        /// should do on the way past: Start() does.
        /// </remarks>
        private OCPPv2_1_GW.TestGatewayNode BuildOCPPNode(OCPPConfiguration Configuration)

            => new (

                   Id:                       NetworkingNode_Id.Parse(Configuration.NodeId ?? OCPPConfiguration.DefaultNodeId),
                   VendorName:               Configuration.VendorName                     ?? OCPPConfiguration.DefaultVendorName,
                   Model:                    Configuration.Model                          ?? OCPPConfiguration.DefaultModel,
                   SerialNumber:             Configuration.SerialNumber,
                   SoftwareVersion:          Configuration.SoftwareVersion                ?? Version,
                   Description:              I18NString.Empty,

                   HTTPAPI_Disabled:               true,
                   HTTPAPI_EventLoggingDisabled:   true,
                   WebAPI_Disabled:                true,

                   ControlWebSocketServer:   null,

                   DisableSendHeartbeats:    true,
                   DisableMaintenanceTasks:  true,

                   DNSClient:                DNSClient,
                   Clock:                    TimeProvider

               );

        #endregion

    }

}
