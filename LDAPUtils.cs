using System;
using System.Linq;
using System.Collections;
using System.Collections.Generic;
using System.Text.RegularExpressions;
using System.Net;
using Novell.Directory.Ldap;
using Novell.Directory.Ldap.Rfc2251;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.DependencyInjection;

namespace eshc_diradmin
{
    public class LDAPUtils
    {
        private readonly ILogger logger;
        Parameters Params;
        LdapConnection Connection;

        public LDAPUtils()
        {
            logger = new Microsoft.Extensions.Logging.Console.ConsoleLogger("ldap", (x, y) => true, true);
        }

        public class Parameters
        {
            public string Host { get; set; } = "localhost";
            public int Port { get; set; } = 389;
            public string RootDN { get; set; } = "dc=directory,dc=eshc,dc=coop";
            public string ManagerDN { get; set; } = "cn=Manager";
            public string ManagerPW { get; set; } = "";

            public string DN(string subdn)
            {
                return subdn + "," + RootDN;
            }
        }

        public void ValidateParametersAndConnect(Parameters prams)
        {
            Params = prams;

            Connection = new LdapConnection { SecureSocketLayer = false };
            Connection.Connect(Params.Host, Params.Port);
            if (!Connection.Connected)
            {
                throw new System.Exception("Could not connect to the LDAP server at " + Params.Host + ":" + Params.Port);
            }
            Connection.Bind(Params.DN(Params.ManagerDN), Params.ManagerPW);
            if (!Connection.Bound)
            {
                throw new System.Exception("Could not bind to the LDAP server with username " + Params.DN(Params.ManagerDN));
            }
        }

        public struct AuthResult
        {
            public bool Valid;
            public bool SuperAdmin;
            public string DN;
            public string DisplayName;
        }


        static Regex ldapFieldRegex = new Regex(@"[ a-zA-Z0-9@_-]*");
        public static bool ValidateLDAPField(string field)
        {
            return ldapFieldRegex.IsMatch(field);
        }

        public LdapSearchQueue Search(string @base, int scope, RfcFilter filter, string[] attrs, bool namesOnly = false)
        {
            LdapMessage m = new LdapSearchRequest(@base, scope, filter, attrs,
                LdapSearchConstraints.DerefAlways, 512, 1, namesOnly, null);
            return (LdapSearchQueue)Connection.SendRequest(m, null);
        }

        public AuthResult Authenticate(string username, string password)
        {
            if (!ValidateLDAPField(username))
            {
                logger.LogWarning("Tried LDAP injection: " + username);
                return new AuthResult { Valid = false };
            }

            RfcFilter query = new RfcFilter();
            var UTF8 = System.Text.Encoding.UTF8;
            query.StartNestedFilter(RfcFilter.And);
            query.AddAttributeValueAssertion(RfcFilter.EqualityMatch, "objectClass", UTF8.GetBytes("inetOrgPerson"));
            query.StartNestedFilter(RfcFilter.Or);
            var usernameBytes = UTF8.GetBytes(username);
            query.AddAttributeValueAssertion(RfcFilter.EqualityMatch, "mailPrimaryAddress", usernameBytes);
            query.AddAttributeValueAssertion(RfcFilter.EqualityMatch, "mail", usernameBytes);
            query.AddAttributeValueAssertion(RfcFilter.EqualityMatch, "uid", usernameBytes);
            query.EndNestedFilter(RfcFilter.Or);
            query.EndNestedFilter(RfcFilter.And);

            var resmq = Search(Params.DN("ou=Members"),
                    LdapConnection.ScopeOne, query, new string[] { "displayName", "memberOf" });

            LdapEntry res = null;
            AuthResult ar = new AuthResult();
            LdapMessage msg;
            while ((msg = resmq.GetResponse()) != null)
            {
                if (msg is LdapSearchResult)
                {
                    LdapEntry r = ((LdapSearchResult)msg).Entry;
                    if (res != null)
                    {
                        logger.LogError("LDAP login returned multiple results: " + username);
                        return new AuthResult { Valid = false };
                    }
                    res = r;
                    logger.LogInformation("LDAP login found user DN: " + res.Dn);
                }
            }
            if (res == null)
            {
                logger.LogError("LDAP login failed to find account: " + username);
                return new AuthResult { Valid = false };
            }

            ar.Valid = false;
            ar.SuperAdmin = false;
            ar.DN = res.Dn;
            ar.DisplayName = res.GetAttribute("displayName").StringValue ?? res.Dn;

            // try login
            using (LdapConnection userConn = new LdapConnection { SecureSocketLayer = false })
            {
                Connection.Connect(Params.Host, Params.Port);
                if (!Connection.Connected)
                {
                    throw new System.Exception("Could not connect to the LDAP server at " + Params.Host + ":" + Params.Port);
                }
                try
                {
                    Connection.Bind(ar.DN, password);
                }
                catch (LdapException)
                {
                    logger.LogError("LDAP login: wrong password for account: " + ar.DN);
                    return new AuthResult { Valid = false };
                }
                if (!Connection.Bound)
                {
                    logger.LogError("LDAP login: could not bind account: " + ar.DN);
                    return new AuthResult { Valid = false };
                }
            }

            var groups = res.GetAttribute("memberOf").StringValueArray;
            ar.Valid = groups.Contains(Params.DN("cn=AllMembers,ou=Groups"));
            ar.SuperAdmin = groups.Contains(Params.DN("cn=InternetSpecialists,ou=Groups"));

            foreach (var group in groups)
            {
                logger.LogDebug("Group: " + group);
            }

            return ar.Valid ? ar : new AuthResult { Valid = false };
        }
    }
}