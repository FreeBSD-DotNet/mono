//
// System.Security.SecurityManager.cs
//
// Authors:
//	Nick Drochak(ndrochak@gol.com)
//	Sebastien Pouliot  <sebastien@ximian.com>
//
// (C) Nick Drochak
// Portions (C) 2004 Motus Technologies Inc. (http://www.motus.com)
// Copyright (C) 2004-2005 Novell, Inc (http://www.novell.com)
//
// Permission is hereby granted, free of charge, to any person obtaining
// a copy of this software and associated documentation files (the
// "Software"), to deal in the Software without restriction, including
// without limitation the rights to use, copy, modify, merge, publish,
// distribute, sublicense, and/or sell copies of the Software, and to
// permit persons to whom the Software is furnished to do so, subject to
// the following conditions:
// 
// The above copyright notice and this permission notice shall be
// included in all copies or substantial portions of the Software.
// 
// THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND,
// EXPRESS OR IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF
// MERCHANTABILITY, FITNESS FOR A PARTICULAR PURPOSE AND
// NONINFRINGEMENT. IN NO EVENT SHALL THE AUTHORS OR COPYRIGHT HOLDERS BE
// LIABLE FOR ANY CLAIM, DAMAGES OR OTHER LIABILITY, WHETHER IN AN ACTION
// OF CONTRACT, TORT OR OTHERWISE, ARISING FROM, OUT OF OR IN CONNECTION
// WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE SOFTWARE.
//

using System.Collections;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Security.Permissions;
using System.Security.Policy;
using System.Text;

using Mono.Xml;

namespace System.Security {

	// Must match MonoDeclSecurityActions in /mono/metadata/reflection.h
	internal struct RuntimeDeclSecurityActions {
		public RuntimeDeclSecurityEntry cas;
		public RuntimeDeclSecurityEntry noncas;
		public RuntimeDeclSecurityEntry choice;
	}

	public sealed class SecurityManager {

		private static object _lockObject;
		private static ArrayList _hierarchy;
		private static PermissionSet _fullTrust; // for [AllowPartiallyTrustedCallers]
		private static IPermission _unmanagedCode;
		private static Hashtable _declsecCache;

		static SecurityManager () 
		{
			// lock(this) is bad
			// http://msdn.microsoft.com/library/en-us/dnaskdr/html/askgui06032003.asp?frame=true
			_lockObject = new object ();
		}

		private SecurityManager ()
		{
		}

		// properties

		extern public static bool CheckExecutionRights {
			[MethodImplAttribute (MethodImplOptions.InternalCall)]
			get;

			[MethodImplAttribute (MethodImplOptions.InternalCall)]
			[SecurityPermission (SecurityAction.Demand, Flags=SecurityPermissionFlag.ControlPolicy)]
			set;
		}

		extern public static bool SecurityEnabled {
			[MethodImplAttribute (MethodImplOptions.InternalCall)]
			get;

			[MethodImplAttribute (MethodImplOptions.InternalCall)]
			[SecurityPermission (SecurityAction.Demand, Flags=SecurityPermissionFlag.ControlPolicy)]
			set;
		}

		// methods

#if NET_2_0
		[MonoTODO]
		[StrongNameIdentityPermission (SecurityAction.LinkDemand, PublicKey = "0x00000000000000000400000000000000")]
		public static void GetZoneAndOrigin (out ArrayList zone, out ArrayList origin) 
		{
			zone = new ArrayList ();
			origin = new ArrayList ();
		}
#endif

		public static bool IsGranted (IPermission perm)
		{
			if (perm == null)
				return true;
			if (!SecurityEnabled)
				return true;

			// - Policy driven
			// - Only check the caller (no stack walk required)
			// - Not affected by overrides (like Assert, Deny and PermitOnly)
			// - calls IsSubsetOf even for non CAS permissions
			//   (i.e. it does call Demand so any code there won't be executed)
			return IsGranted (Assembly.GetCallingAssembly (), perm);
		}

		internal static bool IsGranted (Assembly a, IPermission perm)
		{
			CodeAccessPermission grant = null;

			if (a.GrantedPermissionSet != null) {
				grant = (CodeAccessPermission) a.GrantedPermissionSet.GetPermission (perm.GetType ());
				if (grant == null) {
					if (!a.GrantedPermissionSet.IsUnrestricted () || !(perm is IUnrestrictedPermission)) {
						return false;
					}
				} else if (!perm.IsSubsetOf (grant)) {
					return false;
				}
			}

			if (a.DeniedPermissionSet != null) {
				CodeAccessPermission refuse = (CodeAccessPermission) a.DeniedPermissionSet.GetPermission (perm.GetType ());
				if ((refuse != null) && perm.IsSubsetOf (refuse))
					return false;
			}
			return true;
		}

		internal static bool IsGranted (Assembly a, PermissionSet ps, bool noncas)
		{
			if (ps.IsEmpty ())
				return true;

			foreach (IPermission p in ps) {
				// note: this may contains non CAS permissions
				if ((!noncas) && (p is CodeAccessPermission)) {
					if (!SecurityManager.IsGranted (a, p))
						return false;
				} else {
					// but non-CAS will throw on failure...
					try {
						p.Demand ();
					}
					catch (SecurityException) {
						// ... so we catch
						return false;
					}
				}
			}
			return true;
		}

		[SecurityPermission (SecurityAction.Demand, Flags=SecurityPermissionFlag.ControlPolicy)]
		public static PolicyLevel LoadPolicyLevelFromFile (string path, PolicyLevelType type)
		{
			if (path == null)
				throw new ArgumentNullException ("path");

			PolicyLevel pl = null;
			try {
				pl = new PolicyLevel (type.ToString (), type);
				pl.LoadFromFile (path);
			}
			catch (Exception e) {
				throw new ArgumentException (Locale.GetText ("Invalid policy XML"), e);
			}
			return pl;
		}

		[SecurityPermission (SecurityAction.Demand, Flags=SecurityPermissionFlag.ControlPolicy)]
		public static PolicyLevel LoadPolicyLevelFromString (string str, PolicyLevelType type)
		{
			if (null == str)
				throw new ArgumentNullException ("str");

			PolicyLevel pl = null;
			try {
				pl = new PolicyLevel (type.ToString (), type);
				pl.LoadFromString (str);
			}
			catch (Exception e) {
				throw new ArgumentException (Locale.GetText ("Invalid policy XML"), e);
			}
			return pl;
		}

		[SecurityPermission (SecurityAction.Demand, Flags=SecurityPermissionFlag.ControlPolicy)]
		public static IEnumerator PolicyHierarchy ()
		{
			return Hierarchy;
		}

		public static PermissionSet ResolvePolicy (Evidence evidence)
		{
			// no evidence, no permission
			if (evidence == null)
				return new PermissionSet (PermissionState.None);

			PermissionSet ps = null;
			// Note: can't call PolicyHierarchy since ControlPolicy isn't required to resolve policies
			IEnumerator ple = Hierarchy;
			while (ple.MoveNext ()) {
				PolicyLevel pl = (PolicyLevel) ple.Current;
				if (ResolvePolicyLevel (ref ps, pl, evidence)) {
					break;	// i.e. PolicyStatementAttribute.LevelFinal
				}
			}

			ResolveIdentityPermissions (ps, evidence);
			return ps;
		}

#if NET_2_0
		[MonoTODO ("more tests are needed")]
		public static PermissionSet ResolvePolicy (Evidence[] evidences)
		{
			if ((evidences == null) || (evidences.Length == 0) ||
				((evidences.Length == 1) && (evidences [0].Count == 0))) {
				return new PermissionSet (PermissionState.None);
			}

			// probably not optimal
			PermissionSet ps = ResolvePolicy (evidences [0]);
			for (int i=1; i < evidences.Length; i++) {
				ps = ps.Intersect (ResolvePolicy (evidences [i]));
			}
			return ps;
		}

		public static PermissionSet ResolveSystemPolicy (Evidence evidence)
		{
			// no evidence, no permission
			if (evidence == null)
				return new PermissionSet (PermissionState.None);

			// Note: can't call PolicyHierarchy since ControlPolicy isn't required to resolve policies
			PermissionSet ps = null;
			IEnumerator ple = Hierarchy;
			while (ple.MoveNext ()) {
				PolicyLevel pl = (PolicyLevel) ple.Current;
				if (pl.Type == PolicyLevelType.AppDomain)
					break;
				if (ResolvePolicyLevel (ref ps, pl, evidence))
					break;	// i.e. PolicyStatementAttribute.LevelFinal
			}

			ResolveIdentityPermissions (ps, evidence);
			return ps;
		}
#endif

		static private SecurityPermission _execution = new SecurityPermission (SecurityPermissionFlag.Execution);

		[MonoTODO()]
		public static PermissionSet ResolvePolicy (Evidence evidence, PermissionSet reqdPset, PermissionSet optPset, PermissionSet denyPset, out PermissionSet denied)
		{
			PermissionSet resolved = ResolvePolicy (evidence);
			// do we have the minimal permission requested by the assembly ?
			if ((reqdPset != null) && !reqdPset.IsSubsetOf (resolved)) {
				throw new PolicyException (Locale.GetText (
					"Policy doesn't grant the minimal permissions required to execute the assembly."));
			}
			// do we have the right to execute ?
			if (CheckExecutionRights) {
				// unless we have "Full Trust"...
				if (!resolved.IsUnrestricted ()) {
					// ... we need to find a SecurityPermission
					IPermission security = resolved.GetPermission (typeof (SecurityPermission));
					if (!_execution.IsSubsetOf (security)) {
						throw new PolicyException (Locale.GetText (
							"Policy doesn't grant the right to execute to the assembly."));
					}
				}
			}

			denied = denyPset;
			return resolved;
		}

		public static IEnumerator ResolvePolicyGroups (Evidence evidence)
		{
			if (evidence == null)
				throw new ArgumentNullException ("evidence");

			ArrayList al = new ArrayList ();
			// Note: can't call PolicyHierarchy since ControlPolicy isn't required to resolve policies
			IEnumerator ple = Hierarchy;
			while (ple.MoveNext ()) {
				PolicyLevel pl = (PolicyLevel) ple.Current;
				CodeGroup cg = pl.ResolveMatchingCodeGroups (evidence);
				al.Add (cg);
			}
			return al.GetEnumerator ();
		}

		[SecurityPermission (SecurityAction.Demand, Flags=SecurityPermissionFlag.ControlPolicy)]
		public static void SavePolicy () 
		{
			IEnumerator e = Hierarchy;
			while (e.MoveNext ()) {
				PolicyLevel level = (e.Current as PolicyLevel);
				level.Save ();
			}
		}

		[SecurityPermission (SecurityAction.Demand, Flags=SecurityPermissionFlag.ControlPolicy)]
		public static void SavePolicyLevel (PolicyLevel level) 
		{
			// Yes this will throw a NullReferenceException, just like MS (see FDBK13121)
			level.Save ();
		}

		// private/internal stuff

		private static IEnumerator Hierarchy {
			get {
				// double-lock pattern
				if (_hierarchy == null) {
					lock (_lockObject) {
						if (_hierarchy == null)
							InitializePolicyHierarchy ();
					}
				}
				return _hierarchy.GetEnumerator ();
			}
		}

		private static void InitializePolicyHierarchy ()
		{
			string machinePolicyPath = Path.GetDirectoryName (Environment.GetMachineConfigPath ());
			// note: use InternalGetFolderPath to avoid recursive policy initialization
			string userPolicyPath = Path.Combine (Environment.InternalGetFolderPath (Environment.SpecialFolder.ApplicationData), "mono");

			ArrayList al = new ArrayList ();
			al.Add (new PolicyLevel ("Enterprise", PolicyLevelType.Enterprise,
				Path.Combine (machinePolicyPath, "enterprisesec.config")));

			al.Add (new PolicyLevel ("Machine", PolicyLevelType.Machine,
				Path.Combine (machinePolicyPath, "security.config")));

			al.Add (new PolicyLevel ("User", PolicyLevelType.User,
				Path.Combine (userPolicyPath, "security.config")));

			_hierarchy = ArrayList.Synchronized (al);
		}

		internal static bool ResolvePolicyLevel (ref PermissionSet ps, PolicyLevel pl, Evidence evidence)
		{
			PolicyStatement pst = pl.Resolve (evidence);
			if (pst != null) {
				if (ps == null) {
					// only for initial (first) policy level processed
					ps = pst.PermissionSet;
				} else {
					ps = ps.Intersect (pst.PermissionSet);
					if (ps == null) {
						// null is equals to None - exist that null can throw NullReferenceException ;-)
						ps = new PermissionSet (PermissionState.None);
					}
				}

				if ((pst.Attributes & PolicyStatementAttribute.LevelFinal) == PolicyStatementAttribute.LevelFinal)
					return true;
			}
			return false;
		}

		// TODO: this changes in 2.0 as identity permissions can now be unrestricted
		internal static void ResolveIdentityPermissions (PermissionSet ps, Evidence evidence)
		{
			// Only host evidence are used for policy resolution
			IEnumerator ee = evidence.GetHostEnumerator ();
			while (ee.MoveNext ()) {
				IIdentityPermissionFactory ipf = (ee.Current as IIdentityPermissionFactory);
				if (ipf != null) {
					IPermission p = ipf.CreateIdentityPermission (evidence);
					ps.AddPermission (p);
				}
			}
		}

		internal static PermissionSet Decode (IntPtr permissions, int length)
		{
			// Permission sets from the runtime (declarative security) can be cached
			// for performance as they can never change (i.e. they are read-only).

			if (_declsecCache == null) {
				lock (_lockObject) {
					if (_declsecCache == null) {
						_declsecCache = new Hashtable ();
					}
				}
			}

			PermissionSet ps = null;
			lock (_lockObject) {
				object key = (object) (int) permissions;
				ps = (PermissionSet) _declsecCache [key];
				if (ps == null) {
					// create permissionset and add it to the cache
					byte[] data = new byte [length];
					Marshal.Copy (permissions, data, 0, length);
					ps = Decode (data);
					ps.DeclarativeSecurity = true;
					_declsecCache.Add (key, ps);
				}
			}
			return ps;
		}

		internal static PermissionSet Decode (byte[] encodedPermissions)
		{
			if ((encodedPermissions == null) || (encodedPermissions.Length < 1))
				throw new SecurityException ("Invalid metadata format.");

			switch (encodedPermissions [0]) {
			case 60:
				// Fx 1.0/1.1 declarative security permissions metadata is in Unicode-encoded XML
				string xml = Encoding.Unicode.GetString (encodedPermissions);
				return new PermissionSet (xml);
			case 0x2E:
				// TODO: Fx 2.0
				throw new SecurityException ("Unsupported 2.0 metadata format.");
			default:
				throw new SecurityException ("Unknown metadata format.");
			}
		}

		private static PermissionSet Union (byte[] classPermissions, byte[] methodPermissions)
		{
			if (classPermissions != null) {
				PermissionSet ps = Decode (classPermissions);
				if (methodPermissions != null) {
					ps = ps.Union (Decode (methodPermissions));
				}
				return ps;
			}

			return Decode (methodPermissions);
		}

		//  security check when using reflection

		internal static void ReflectedLinkDemand ()
		{
			// TODO - get the declarative LinkDemand permission set
			PermissionSet ps = null;

			Assembly corlib = typeof (int).Assembly;
			// find the real caller of the icall
			foreach (SecurityFrame f in SecurityFrame.GetStack (0)) {
				// ignore System.Reflection class in corlib
				if ((f.Assembly != corlib) || !f.Method.Name.StartsWith ("System.Reflection")) {
					if (!SecurityManager.IsGranted (f.Assembly, ps, false))
						LinkDemandSecurityException (1, f.Assembly, f.Method);
				}
			}
		}

		// internal - get called at JIT time

		private unsafe static bool LinkDemand (Assembly a, RuntimeDeclSecurityActions *klass, RuntimeDeclSecurityActions *method)
		{
			try {
				PermissionSet ps = null;
				bool result = true;
				if (klass->cas.size > 0) {
					ps = Decode (klass->cas.blob, klass->cas.size);
					result = SecurityManager.IsGranted (a, ps, false);
				}
				if (klass->noncas.size > 0) {
					ps = Decode (klass->noncas.blob, klass->noncas.size);
					result = SecurityManager.IsGranted (a, ps, true);
				}

				if (method->cas.size > 0) {
					ps = Decode (method->cas.blob, method->cas.size);
					result = SecurityManager.IsGranted (a, ps, false);
				}
				if (method->noncas.size > 0) {
					ps = Decode (method->noncas.blob, method->noncas.size);
					result = SecurityManager.IsGranted (a, ps, true);
				}

				// TODO LinkDemandChoice (2.0)
				return result;
			}
			catch (SecurityException) {
				return false;
			}
		}

		private static bool LinkDemandFullTrust (Assembly a)
		{
			// double-lock pattern
			if (_fullTrust == null) {
				lock (_lockObject) {
					if (_fullTrust == null)
						_fullTrust = new NamedPermissionSet ("FullTrust");
				}
			}

			try {
				return SecurityManager.IsGranted (a, _fullTrust, false);
			}
			catch (SecurityException) {
				return false;
			}
		}

		private static bool LinkDemandUnmanaged (Assembly a)
		{
			// double-lock pattern
			if (_unmanagedCode == null) {
				lock (_lockObject) {
					if (_unmanagedCode == null)
						_unmanagedCode = new SecurityPermission (SecurityPermissionFlag.UnmanagedCode);
				}
			}

			return IsGranted (a, _unmanagedCode);
		}

		// we try to provide as much details as possible to help debugging
		private static void LinkDemandSecurityException (int securityViolation, Assembly a, MethodInfo method)
		{
			string message = null;
			AssemblyName an = null;
			PermissionSet granted = null;
			PermissionSet refused = null;
			object demanded = null;
			IPermission failed = null;

			if (a != null) {
				an = a.GetName ();
				granted = a.GrantedPermissionSet;
				refused = a.DeniedPermissionSet;
			}

			switch (securityViolation) {
			case 1: // MONO_JIT_LINKDEMAND_PERMISSION
				message = Locale.GetText ("Permissions refused to call this method.");
				break;
			case 2: // MONO_JIT_LINKDEMAND_APTC
				message = Locale.GetText ("Partially trusted callers aren't allowed to call into this assembly.");
				demanded = (object) _fullTrust;
				break;
			case 4: // MONO_JIT_LINKDEMAND_ECMA
				message = Locale.GetText ("Calling internal calls is restricted to ECMA signed assemblies.");
				break;
			case 8: // MONO_JIT_LINKDEMAND_PINVOKE
				message = Locale.GetText ("Calling unmanaged code isn't allowed from this assembly.");
				demanded = (object) _unmanagedCode;
				failed = _unmanagedCode;
				break;
			default:
				message = Locale.GetText ("JIT time LinkDemand failed.");
				break;
			}

			throw new SecurityException (message, an, granted, refused, method, SecurityAction.LinkDemand, demanded, failed, null);
		}

		private static void InheritanceDemandSecurityException (int securityViolation, Assembly a, Type t, MethodInfo method)
		{
			string message = null;
			AssemblyName an = null;
			PermissionSet granted = null;
			PermissionSet refused = null;

			if (a != null) {
				an = a.GetName ();
				granted = a.GrantedPermissionSet;
				refused = a.DeniedPermissionSet;
			}

			switch (securityViolation) {
			case 1: // MONO_METADATA_INHERITANCEDEMAND_CLASS
				message = String.Format (Locale.GetText ("Class inheritance refused for {0}."), t);
				break;
			case 2: // MONO_METADATA_INHERITANCEDEMAND_CLASS
				message = Locale.GetText ("Method override refused.");
				break;
			default:
				message = Locale.GetText ("Load time InheritDemand failed.");
				break;
			}

			throw new SecurityException (message, an, granted, refused, method, SecurityAction.LinkDemand, null, null, null);
		}

		// internal - get called by the class loader

		// Called when
		// - class inheritance
		// - method overrides
		private unsafe static bool InheritanceDemand (Assembly a, RuntimeDeclSecurityActions *actions)
		{
			try {
				PermissionSet ps = null;
				bool result = true;
				if (actions->cas.size > 0) {
					ps = Decode (actions->cas.blob, actions->cas.size);
					result = SecurityManager.IsGranted (a, ps, false);
				}
				if (actions->noncas.size > 0) {
					ps = Decode (actions->noncas.blob, actions->noncas.size);
					result = SecurityManager.IsGranted (a, ps, true);
				}

				// TODO InheritanceDemandChoice (2.0)
				return result;
			}
			catch (SecurityException) {
				return false;
			}
		}

		// internal - get called by JIT generated code

		private static void InternalDemand (IntPtr permissions, int length)
		{
			PermissionSet ps = Decode (permissions, length);
			ps.Demand ();
		}

		private static void InternalDemandChoice (IntPtr permissions, int length)
		{
#if NET_2_0
			PermissionSet ps = Decode (permissions, length);
			// TODO
#else
			throw new SecurityException ("SecurityAction.DemandChoice is only possible in 2.0");
#endif
		}
	}
}
