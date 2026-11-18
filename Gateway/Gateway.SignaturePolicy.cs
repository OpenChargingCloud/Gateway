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

using cloud.charging.open.protocols.WWCP.Node.Web;

using OCPPv2_1_GW = cloud.charging.open.protocols.OCPPv2_1.Gateway;

#endregion

namespace cloud.charging.open.Gateway
{

    /// <summary>
    /// Who may sign what is sent to this gateway itself: the roles of its web
    /// page and its command line (white paper "Signed OCPP Messages and Signer
    /// Roles", section 5.3, phase 3 of chapter 7).
    /// </summary>
    public partial class Gateway
    {

        #region (private) AttachSignaturePolicy(Node)

        /// <summary>
        /// Give the node's signature policy what it needs to hold a signer
        /// against the policy table: the direction of what this gateway is
        /// sent, what each of its roles may do, and what each action needs.
        /// </summary>
        /// <remarks>
        /// <para>
        /// Only the node's own policy, which decides what is addressed to this
        /// gateway. What it forwards goes through the policy of the forwarding,
        /// and is held by the station it goes to against the station's own
        /// resources.
        /// </para>
        /// <para>
        /// The direction is a station's: the line up to the CSMS is the one a
        /// station has, and an entry a CSMS writes for what it sends its
        /// stations is about this gateway too.
        /// </para>
        /// <para>
        /// Nothing of it is looked at while the mode of the policy is "Off",
        /// the default: this gateway then answers its CSMS as before it had a
        /// table.
        /// </para>
        /// </remarks>
        private void AttachSignaturePolicy(OCPPv2_1_GW.TestGatewayNode Node)
        {

            var policy = Node.OCPP.SignaturePolicy;

            policy.IncomingDirection    ??= "CSMS->CS";
            policy.PermissionsOfRole    ??= PermissionsOfRole;

            // The entries of SetVariables and GetVariables are decided one by one,
            // by the node (ActionsDecidedPerEntry), each by its component's resource.
            policy.ResourceOfComponent    = ResourceOfComponent;

            if (!policy.ActionPermissions.Any())
                policy.AddActionPermissions(GatewayActionPermissions.Default());

        }

        #endregion

        #region (private static) ResourceOfComponent(Component)

        /// <summary>
        /// The resource of this gateway's role model a component of its own
        /// device model belongs to.
        /// </summary>
        /// <remarks>
        /// OCPPServerController is the port the charging stations connect to,
        /// StationLoginController and StationLoginGroupController the stations
        /// that may sign in to it and their login groups: "stations", as their
        /// page. Every other component is what it is for every node.
        /// </remarks>
        private static String ResourceOfComponent(String Component)

            => Component switch {
                   "OCPPServerController"         => GatewayAccess.Stations,
                   "StationLoginController"       => GatewayAccess.Stations,
                   "StationLoginGroupController"  => GatewayAccess.Stations,
                   _                              => protocols.WWCP.SignaturePolicy.DefaultResourceOfComponent(Component)
               };

        #endregion

        #region (private) PermissionsOfRole(RoleName)

        /// <summary>
        /// What the role of the given name may do here, spelt out: the roles of
        /// this gateway as its configuration file leaves them, and nothing for a
        /// name that is none of them.
        /// </summary>
        private IEnumerable<String> PermissionsOfRole(String RoleName)

            => Access.RoleNamed(RoleName) is Role role
                   ? Access.PermissionsOf([ role ]).Select(permission => permission.ToString())
                   : [];

        #endregion

    }

}
