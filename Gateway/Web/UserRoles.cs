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

using System.Diagnostics.CodeAnalysis;

#endregion

using org.GraphDefined.Vanaheimr.Hermod.HTTP;

namespace cloud.charging.open.Gateway.Web
{

    /// <summary>
    /// What somebody signed in to this gateway is allowed to do.
    /// </summary>
    /// <remarks>
    /// Flags rather than a list, because a permission is asked about one at a
    /// time and answered by a single test - and because the set a role grants
    /// is then a constant instead of a collection to be built and searched.
    /// </remarks>
    [Flags]
    public enum Permissions : UInt32
    {

        /// <summary>
        /// Nothing at all. What an unknown role would grant.
        /// </summary>
        None                   = 0,

        /// <summary>
        /// See how this gateway is configured.
        /// </summary>
        ReadConfiguration      = 1,

        /// <summary>
        /// Change how this gateway reaches the network: its name resolution
        /// and where it reads the time.
        /// </summary>
        ChangeNetworkSettings  = 2,

        /// <summary>
        /// Make this gateway ask a name server or a time server something, to
        /// find out whether it can.
        /// </summary>
        /// <remarks>
        /// Its own permission and not part of reading: a diagnostic sends
        /// traffic from this gateway to a host somebody names, which is more
        /// than it sounds like to hand to everybody who may look at a page.
        /// </remarks>
        RunDiagnostics         = 4

    }


    /// <summary>
    /// A role somebody signs in as: a name, and the permissions it carries.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A closed set, and deliberately so: a role this gateway has never heard
    /// of is a role it cannot enforce. So a group whose name is not one of
    /// these grants nothing, rather than quietly granting something - or, far
    /// worse, being taken for a known one because it looks similar.
    /// </para>
    /// <para>
    /// Each role is a user group in the HTTPExt API, under the same name, and
    /// membership of that group is what carries the permissions below. The
    /// permissions stay here because they are this gateway's own vocabulary:
    /// the HTTPExt API knows users, groups and organizations, and has no
    /// opinion about what "may change the name servers" means. So it answers
    /// who somebody is and this answers what that lets them do.
    /// </para>
    /// </remarks>
    /// <param name="Name">The role, and the name of the user group that carries it.</param>
    /// <param name="Permissions">What it grants.</param>
    public sealed record UserRole(String       Name,
                                  Permissions  Permissions)
    {

        #region Properties

        /// <summary>
        /// The user group in the HTTPExt API whose members hold this role.
        /// </summary>
        public UserGroup_Id  GroupId
            => UserGroup_Id.Parse(Name);

        #endregion


        #region Data

        /// <summary>
        /// May look at this gateway, and do nothing to it.
        /// </summary>
        public static readonly UserRole  Viewer       = new ("viewer",
                                                             Permissions.ReadConfiguration);

        /// <summary>
        /// Whoever runs this gateway day to day: may ask whether the network
        /// works - but may not repoint it at other name and time servers.
        /// </summary>
        public static readonly UserRole  Operator     = new ("operator",
                                                             Permissions.ReadConfiguration      |
                                                             Permissions.RunDiagnostics);

        /// <summary>
        /// Everything this gateway can be told, by whoever is trusted with all
        /// of it at once.
        /// </summary>
        public static readonly UserRole  SystemAdmin  = new ("systemadmin",
                                                             Permissions.ReadConfiguration      |
                                                             Permissions.ChangeNetworkSettings  |
                                                             Permissions.RunDiagnostics);

        /// <summary>
        /// Every role this gateway knows.
        /// </summary>
        public static readonly IReadOnlyList<UserRole>  All = [ Viewer, Operator, SystemAdmin ];

        #endregion


        #region (static) TryParse(Text, out Role, out Error)

        /// <summary>
        /// A role by the name the login file writes it under, in any case.
        /// </summary>
        public static Boolean TryParse(String?                             Text,
                                       [NotNullWhen(true)]  out UserRole?  Role,
                                       [NotNullWhen(false)] out String?    Error)
        {

            Role   = All.FirstOrDefault(role => String.Equals(role.Name, Text?.Trim(), StringComparison.OrdinalIgnoreCase));

            Error  = Role is null
                         ? $"\"{Text}\" is not a role this gateway knows. Known roles: {String.Join(", ", All.Select(role => role.Name))}."
                         : null;

            return Role is not null;

        }

        #endregion

        #region (override) ToString()

        public override String ToString()
            => Name;

        #endregion

    }


    /// <summary>
    /// What a set of roles adds up to.
    /// </summary>
    public static class UserRoleExtensions
    {

        #region PermissionsOf(this Roles)

        /// <summary>
        /// Everything the given roles grant together.
        /// </summary>
        public static Permissions PermissionsOf(this IEnumerable<UserRole> Roles)
        {

            var permissions = Permissions.None;

            foreach (var role in Roles)
                permissions |= role.Permissions;

            return permissions;

        }

        #endregion

        #region Names(this Permissions)

        /// <summary>
        /// The permissions as the web interface reads them, so that a page can
        /// grey out what this browser may not do instead of finding out by
        /// being refused.
        /// </summary>
        /// <remarks>
        /// What the browser is told is a copy of what the gateway enforces, and
        /// not the enforcement: every request is checked again on arrival. A
        /// greyed-out button is a courtesy, not a lock.
        /// </remarks>
        public static IEnumerable<String> Names(this Permissions Permissions)

            => Enum.GetValues<Permissions>().
                    Where (permission => permission != Web.Permissions.None && Permissions.HasFlag(permission)).
                    Select(permission => Char.ToLowerInvariant(permission.ToString()[0]) + permission.ToString()[1..]);

        #endregion

    }

}
