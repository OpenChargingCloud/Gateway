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

namespace cloud.charging.open.Gateway
{

    /// <summary>
    /// Registering with the CSMS, as every node that serves OCPP registers -
    /// see OCPPServingNode.Registration.cs: the BootNotification of the OCPP
    /// node of this gateway once a connection to its back end is up, whoever
    /// dialled, and its heartbeats once it was accepted.
    /// </summary>
    public partial class Gateway
    {

        #region (protected override) BootNotificationIdentity(Node)

        /// <summary>
        /// Who this gateway says it is in its BootNotification: what its OCPP
        /// node says of itself.
        /// </summary>
        protected override cloud.charging.open.protocols.OCPPv2_1.ChargingStation BootNotificationIdentity(cloud.charging.open.protocols.OCPPv2_1.NetworkingNode.AOCPPNetworkingNode Node)

            => new (
                   gw01.Model,
                   gw01.VendorName,
                   gw01.SerialNumber,
                   gw01.SoftwareVersion ?? Version
               );

        #endregion

    }

}
