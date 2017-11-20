﻿using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Text;

using UnityEditor;
using UnityEngine;

namespace NativeScript
{
	/// <summary>
	/// Code generator that reads a JSON file and outputs C# and C++ code
	/// bindings so C++ can call managed functions and MonoBehaviour "messages"
	/// like Update() can call their C++ counterparts.
	/// </summary>
	/// <author>
	/// Jackson Dunstan, 2017, http://JacksonDunstan.com
	/// </author>
	/// <license>
	/// MIT
	/// </license>
	public static class GenerateBindings
	{
		// Disable unused field types. JsonUtility actually uses them, but it
		// does so with reflection.
		#pragma warning disable CS0649
		
		[Serializable]
		class JsonConstructor
		{
			public string[] ParamTypes;
			public string[] Exceptions;
		}
		
		[Serializable]
		class JsonGenericParams
		{
			public string[] Types;
			public int MaxSimultaneous;
		}
		
		[Serializable]
		class JsonMethod
		{
			public string Name;
			public string[] ParamTypes;
			public JsonGenericParams[] GenericParams;
			public bool IsReadOnly;
			public string[] Exceptions;
		}
		
		[Serializable]
		class JsonPropertyGet
		{
			public bool IsReadOnly = true;
			public string[] ParamTypes;
			public string[] Exceptions;
		}
		
		[Serializable]
		class JsonPropertySet
		{
			public bool IsReadOnly;
			public string[] ParamTypes;
			public string[] Exceptions;
		}
		
		[Serializable]
		class JsonProperty
		{
			public string Name;
			public JsonPropertyGet Get;
			public JsonPropertySet Set;
		}
		
		[Serializable]
		class JsonEvent
		{
			public string Name;
		}
		
		[Serializable]
		class JsonType
		{
			public string Name;
			public JsonConstructor[] Constructors;
			public JsonMethod[] Methods;
			public JsonProperty[] Properties;
			public string[] Fields;
			public JsonEvent[] Events;
			public JsonGenericParams[] GenericParams;
			public int MaxSimultaneous;
		}
		
		[Serializable]
		class JsonBaseType
		{
			public string Name;
			public JsonGenericParams[] GenericParams;
			public int MaxSimultaneous;
			public JsonMethod[] OverrideMethods;
		}
		
		[Serializable]
		class JsonMonoBehaviour
		{
			public string Name;
			public string[] Messages;
		}
		
		[Serializable]
		class JsonArray
		{
			public string Type;
			public int[] Ranks;
		}
		
		[Serializable]
		class JsonDelegate
		{
			public string Type;
			public JsonGenericParams[] GenericParams;
			public int MaxSimultaneous;
		}
		
		[Serializable]
		class JsonDocument
		{
			public string[] Assemblies;
			public JsonType[] Types;
			public JsonBaseType[] BaseTypes;
			public JsonMonoBehaviour[] MonoBehaviours;
			public JsonArray[] Arrays;
			public JsonDelegate[] Delegates;
		}
		
		const int InitialStringBuilderCapacity = 1024 * 100;
		
		class StringBuilders
		{
			public readonly StringBuilder CsharpInitParams =
				new StringBuilder(InitialStringBuilderCapacity);
			public readonly StringBuilder CsharpDelegateTypes =
				new StringBuilder(InitialStringBuilderCapacity);
			public readonly StringBuilder CsharpStructStoreInitCalls =
				new StringBuilder(InitialStringBuilderCapacity);
			public readonly StringBuilder CsharpInitCall =
				new StringBuilder(InitialStringBuilderCapacity);
			public readonly StringBuilder CsharpBaseTypes =
				new StringBuilder(InitialStringBuilderCapacity);
			public readonly StringBuilder CsharpFunctions =
				new StringBuilder(InitialStringBuilderCapacity);
			public readonly StringBuilder CsharpMonoBehaviours =
				new StringBuilder(InitialStringBuilderCapacity);
			public readonly StringBuilder CsharpDelegates =
				new StringBuilder(InitialStringBuilderCapacity);
			public readonly StringBuilder CsharpImports =
				new StringBuilder(InitialStringBuilderCapacity);
			public readonly StringBuilder CsharpGetDelegateCalls =
				new StringBuilder(InitialStringBuilderCapacity);
			public readonly StringBuilder CppFunctionPointers =
				new StringBuilder(InitialStringBuilderCapacity);
			public readonly StringBuilder CppTypeDeclarations =
				new StringBuilder(InitialStringBuilderCapacity);
			public readonly StringBuilder CppTypeDefinitions =
				new StringBuilder(InitialStringBuilderCapacity);
			public readonly StringBuilder CppMethodDefinitions =
				new StringBuilder(InitialStringBuilderCapacity);
			public readonly StringBuilder CppInitParams =
				new StringBuilder(InitialStringBuilderCapacity);
			public readonly StringBuilder CppInitBody =
				new StringBuilder(InitialStringBuilderCapacity);
			public readonly StringBuilder CppMonoBehaviourMessages =
				new StringBuilder(InitialStringBuilderCapacity);
			public readonly StringBuilder CppGlobalStateAndFunctions =
				new StringBuilder(InitialStringBuilderCapacity);
			public readonly StringBuilder CppBoxingMethodDeclarations =
				new StringBuilder(InitialStringBuilderCapacity);
			public readonly StringBuilder TempStrBuilder =
				new StringBuilder(InitialStringBuilderCapacity);
		}
		
		class ParameterInfo
		{
			public string Name;
			public Type ParameterType;
			public Type DereferencedParameterType;
			public bool IsOut;
			public bool IsRef;
			public TypeKind Kind;
			public bool IsVirtual;
		}
		
		enum TypeKind
		{
			// No type (e.g. a global function)
			None,
			
			// An instance of any class
			Class,
			
			// A struct that must be managed. This includes types like
			// RaycastHit which have class fields (Transform) and types with no
			// C++ equivalent like decimal.
			ManagedStruct,
			
			// A struct that can be copied between C#/C++. These are types like
			// Vector3 with only non-class fields and a C++ equivalent can be
			// generated.
			FullStruct,
			
			// Any enum
			Enum,
			
			// Any primitive (e.g. int) except pointers
			Primitive,
			
			// A pointer to any type, either X*, IntPtr, or UIntPtr
			Pointer
		}
		
		// Compares by field declaration order
		// This uses MetadataToken, which isn't guaranteed to match field
		// declaration order. It just happens to on Mono and .NET.
		class FieldOrderComparer : IComparer
		{
			int IComparer.Compare(object x, object y)
			{
				FieldInfo xField = (FieldInfo)x;
				FieldInfo yField = (FieldInfo)y;
				return xField.MetadataToken < yField.MetadataToken
					? -1
					: xField.MetadataToken > yField.MetadataToken
						? 1
						: 0;
			}
		}
		
		class MessageInfo
		{
			public readonly string Name;
			public readonly Type[] ParameterTypes;
			public bool Selected;
			
			public MessageInfo(
				string name,
				params Type[] parameterTypes)
			{
				Name = name;
				ParameterTypes = parameterTypes;
			}
		}
		
		static readonly MessageInfo[] messageInfos = new[] {
			new MessageInfo("Awake"),
			new MessageInfo("FixedUpdate"),
			new MessageInfo("LateUpdate"),
			new MessageInfo("OnAnimatorIK", typeof(int)),
			new MessageInfo("OnAnimatorMove"),
			new MessageInfo("OnApplicationFocus", typeof(bool)),
			new MessageInfo("OnApplicationPause", typeof(bool)),
			new MessageInfo("OnApplicationQuit"),
			new MessageInfo("OnAudioFilterRead", typeof(float[]), typeof(int)),
			new MessageInfo("OnBecameInvisible"),
			new MessageInfo("OnBecameVisible"),
			new MessageInfo("OnCollisionEnter", typeof(Collision)),
			new MessageInfo("OnCollisionEnter2D", typeof(Collision2D)),
			new MessageInfo("OnCollisionExit", typeof(Collision)),
			new MessageInfo("OnCollisionExit2D", typeof(Collision2D)),
			new MessageInfo("OnCollisionStay", typeof(Collision)),
			new MessageInfo("OnCollisionStay2D", typeof(Collision2D)),
			new MessageInfo("OnConnectedToServer"),
			new MessageInfo("OnControllerColliderHit", typeof(ControllerColliderHit)),
			new MessageInfo("OnDestroy"),
			new MessageInfo("OnDisable"),
			new MessageInfo("OnDisconnectedFromServer", typeof(NetworkDisconnection)),
			new MessageInfo("OnDrawGizmos"),
			new MessageInfo("OnDrawGizmosSelected"),
			new MessageInfo("OnEnable"),
			new MessageInfo("OnFailedToConnect", typeof(NetworkConnectionError)),
			new MessageInfo("OnFailedToConnectToMasterServer", typeof(NetworkConnectionError)),
			new MessageInfo("OnGUI"),
			new MessageInfo("OnJointBreak", typeof(float)),
			new MessageInfo("OnJointBreak2D", typeof(Joint2D)),
			new MessageInfo("OnMasterServerEvent", typeof(MasterServerEvent)),
			new MessageInfo("OnMouseDown"),
			new MessageInfo("OnMouseDrag"),
			new MessageInfo("OnMouseEnter"),
			new MessageInfo("OnMouseExit"),
			new MessageInfo("OnMouseOver"),
			new MessageInfo("OnMouseUp"),
			new MessageInfo("OnMouseUpAsButton"),
			new MessageInfo("OnNetworkInstantiate", typeof(NetworkMessageInfo)),
			new MessageInfo("OnParticleCollision", typeof(GameObject)),
			new MessageInfo("OnParticleTrigger"),
			new MessageInfo("OnPlayerConnected", typeof(NetworkPlayer)),
			new MessageInfo("OnPlayerDisconnected", typeof(NetworkPlayer)),
			new MessageInfo("OnPostRender"),
			new MessageInfo("OnPreCull"),
			new MessageInfo("OnPreRender"),
			new MessageInfo("OnRenderImage", typeof(RenderTexture), typeof(RenderTexture)),
			new MessageInfo("OnRenderObject"),
			new MessageInfo("OnSerializeNetworkView", typeof(BitStream), typeof(NetworkMessageInfo)),
			new MessageInfo("OnServerInitialized"),
			new MessageInfo("OnTransformChildrenChanged"),
			new MessageInfo("OnTransformParentChanged"),
			new MessageInfo("OnTriggerEnter", typeof(Collider)),
			new MessageInfo("OnTriggerEnter2D", typeof(Collider2D)),
			new MessageInfo("OnTriggerExit", typeof(Collider)),
			new MessageInfo("OnTriggerExit2D", typeof(Collider2D)),
			new MessageInfo("OnTriggerStay", typeof(Collider)),
			new MessageInfo("OnTriggerStay2D", typeof(Collider2D)),
			new MessageInfo("OnValidate"),
			new MessageInfo("OnWillRenderObject"),
			new MessageInfo("Reset"),
			new MessageInfo("Start"),
			new MessageInfo("Update"),
		};
		
		private static readonly Type[] PRIMITIVE_TYPES = {
			typeof(bool),
			typeof(sbyte),
			typeof(byte),
			typeof(short),
			typeof(ushort),
			typeof(int),
			typeof(uint),
			typeof(long),
			typeof(ulong),
			typeof(char),
			typeof(float),
			typeof(double),
		};
		
		const string PostCompileWorkPref = "NativeScriptGenerateBindingsPostCompileWork";
		
		static readonly string DotNetDllsDirPath = new FileInfo(
				new Uri(typeof(string).Assembly.CodeBase).LocalPath
			).DirectoryName;
		static readonly string UnityDllsDirPath = new FileInfo(
				new Uri(typeof(GameObject).Assembly.CodeBase).LocalPath
			).DirectoryName;
		static readonly string AssetsDirPath = Application.dataPath;
		static readonly string ProjectDirPath =
			new DirectoryInfo(AssetsDirPath)
				.Parent
				.FullName;
		static readonly string CppDirPath =
			Path.Combine(
				Path.Combine(
					ProjectDirPath,
					"CppSource"),
				"NativeScript");
		static readonly string CsharpPath = Path.Combine(
			AssetsDirPath,
			Path.Combine(
				"NativeScript",
				"Bindings.cs"));
		static readonly string CppHeaderPath = Path.Combine(
			CppDirPath,
			"Bindings.h");
		static readonly string CppSourcePath = Path.Combine(
			CppDirPath,
			"Bindings.cpp");
		
		static readonly FieldOrderComparer DefaultFieldOrderComparer
			= new FieldOrderComparer();
		
		// Restore unused field types
		#pragma warning restore CS0649
		
		[MenuItem("NativeScript/Generate Bindings #%g")]
		public static void Generate()
		{
			EditorPrefs.DeleteKey(PostCompileWorkPref);
			JsonDocument doc = LoadJson();
			Assembly[] assemblies = GetAssemblies(doc.Assemblies);
			
			// Determine whether we need to generate stubs
			// We can skip this step if we've already generated all the
			// required MonoBehaviour classes and their messages
			bool needStubs = false;
			foreach (JsonMonoBehaviour monoBehaviour in doc.MonoBehaviours)
			{
				// Check if the MonoBehaviour type is already generated
				Type type = TryGetType(
					monoBehaviour.Name,
					assemblies);
				if (type == null)
				{
					needStubs = true;
					break;
				}
				
				// Check if all the messages are already generated
				foreach (string message in monoBehaviour.Messages)
				{
					MethodInfo methodInfo = type.GetMethod(message);
					if (methodInfo == null)
					{
						needStubs = true;
						goto determinedNeedStubs;
					}
				}
			}
			determinedNeedStubs:;
			
			if (needStubs)
			{
				// We'll need to be able to get these via reflection later
				StringBuilder csharpMonoBehaviours = new StringBuilder(
					InitialStringBuilderCapacity);
				string timestamp = DateTime.Now.ToLongTimeString();
				AppendStubMonoBehaviours(
					doc.MonoBehaviours,
					timestamp,
					csharpMonoBehaviours);
				
				// Inject
				string csharpContents = File.ReadAllText(CsharpPath);
				csharpContents = InjectIntoString(
					csharpContents,
					"/*BEGIN MONOBEHAVIOURS*/\n",
					"\n/*END MONOBEHAVIOURS*/",
					csharpMonoBehaviours.ToString());
				File.WriteAllText(CsharpPath, csharpContents);
				
				// Compile and continue after scripts are refreshed
				Debug.Log("Waiting for compile...");
				AssetDatabase.Refresh();
				EditorPrefs.SetBool(PostCompileWorkPref, true);
			}
			else
			{
				DoPostCompileWork(true);
			}
		}
		
		static void AppendStubMonoBehaviours(
			JsonMonoBehaviour[] monoBehaviours,
			string timestamp,
			StringBuilder output)
		{
			if (monoBehaviours != null)
			{
				foreach (JsonMonoBehaviour jsonMonoBehaviour in monoBehaviours)
				{
					// Split namespace from name
					string fullName = jsonMonoBehaviour.Name;
					string monoBehaviourName;
					string monoBehaviourNamespace;
					int index = fullName.LastIndexOf('.');
					if (index >= 0)
					{
						monoBehaviourNamespace = fullName.Substring(
							0,
							index);
						monoBehaviourName = fullName.Substring(
							index + 1);
					}
					else
					{
						monoBehaviourName = fullName;
						monoBehaviourNamespace = string.Empty;
					}
					
					int indent = AppendNamespaceBeginning(
						monoBehaviourNamespace,
						output);
					AppendIndent(indent, output);
					output.Append("public class ");
					output.Append(monoBehaviourName);
					output.Append(" : UnityEngine.MonoBehaviour\n");
					AppendIndent(indent, output);
					output.Append("{\n");
					AppendIndent(indent + 1, output);
					output.Append("// Stub version. GenerateBindings is still in progress. ");
					output.Append(timestamp);
					output.Append('\n');
					AppendIndent(indent, output);
					output.Append("}\n");
					AppendNamespaceEnding(indent, output);
				}
			}
		}
		
		[UnityEditor.Callbacks.DidReloadScripts]
		static void OnScriptsReloaded()
		{
			// Scripts get reloaded for many reasons, not just our work
			// Check if this reload is due to us refreshing the asset DB
			bool doWork = EditorPrefs.GetBool(PostCompileWorkPref, false);
			EditorPrefs.DeleteKey(PostCompileWorkPref);
			if (doWork)
			{
				DoPostCompileWork(false);
			}
		}
		
		static void DoPostCompileWork(bool canRefreshAssetDb)
		{
			DateTime beforeTime = DateTime.Now;
			
			JsonDocument doc = LoadJson();
			Assembly[] assemblies = GetAssemblies(doc.Assemblies);
			StringBuilders builders = new StringBuilders();
			
			// Generate types
			if (doc.Types != null)
			{
				foreach (JsonType jsonType in doc.Types)
				{
					AppendType(
						jsonType,
						assemblies,
						builders);
				}
			}

			// Generate base types
			if (doc.BaseTypes != null)
			{
				foreach (JsonBaseType jsonBaseType in doc.BaseTypes)
				{
					AppendBaseType(
						jsonBaseType,
						assemblies,
						builders);
				}
			}

			// Generate boxing and unboxing for primitive types
			foreach (Type type in PRIMITIVE_TYPES)
			{
				AppendBoxingUnboxing(
					type,
					TypeKind.Primitive,
					null,
					builders);
			}
			
			// Generate MonoBehaviours
			if (doc.MonoBehaviours != null)
			{
				foreach (JsonMonoBehaviour monoBehaviour in doc.MonoBehaviours)
				{
					AppendMonoBehaviour(
						monoBehaviour,
						assemblies,
						builders);
				}
			}
			
			// Generate arrays
			if (doc.Arrays != null)
			{
				foreach (JsonArray array in doc.Arrays)
				{
					AppendArray(
						array,
						assemblies,
						builders);
				}
			}
			
			if (doc.Delegates != null)
			{
				foreach (JsonDelegate del in doc.Delegates)
				{
					AppendDelegate(
						del,
						assemblies,
						builders);
				}
			}
			
			// Generate exception setters
			AppendExceptions(
				doc,
				assemblies,
				builders);
			
			RemoveTrailingChars(builders);
			
			InjectBuilders(builders);
			if (canRefreshAssetDb)
			{
				AssetDatabase.Refresh();
				DateTime afterTime = DateTime.Now;
				TimeSpan duration = afterTime - beforeTime;
				Debug.LogFormat(
					"Done generating bindings in {0} seconds.",
					duration.TotalSeconds);
			}
			else
			{
				Debug.LogWarning(
					"Can't auto-refresh due to a bug in Unity. " +
					"Please manually refresh assets with " +
					"Assets -> Refresh to finish generating bindings");
			}
		}
		
		static JsonDocument LoadJson()
		{
			string jsonPath = Path.Combine(
				Application.dataPath,
				NativeScriptConstants.ExposedTypesJsonPath);
			string json = File.ReadAllText(jsonPath);
			return JsonUtility.FromJson<JsonDocument>(json);
		}
		
		static Assembly[] GetAssemblies(string[] assemblyNames)
		{
			const int numDefaultAssemblies =
#if UNITY_2017_2_OR_NEWER
				43;
#else
				7;
#endif
			
			int numAssemblies;
			Assembly[] assemblies;
			if (assemblyNames == null)
			{
				numAssemblies = numDefaultAssemblies;
				assemblies = new Assembly[numAssemblies];
			}
			else
			{
				numAssemblies = numDefaultAssemblies + assemblyNames.Length;
				assemblies = new Assembly[numAssemblies];
				
				for (int i = 0; i < assemblyNames.Length; ++i)
				{
					string path = assemblyNames[i]
						.Replace("UNITY_PROJECT", ProjectDirPath)
						.Replace("UNITY_ASSETS", AssetsDirPath)
						.Replace("DOTNET_DLLS", DotNetDllsDirPath)
						.Replace("UNITY_DLLS", UnityDllsDirPath);
					Assembly assembly = Assembly.LoadFrom(path);
					assemblies[numDefaultAssemblies + i] = assembly;
				}
			}
			assemblies[0] = typeof(string).Assembly; // .NET: mscorlib
			assemblies[1] = typeof(Uri).Assembly; // .NET: System
			assemblies[2] = typeof(Action).Assembly; // .NET: System.Core
			assemblies[3] = typeof(Vector3).Assembly; // UnityEngine (core module for 2017.2+)
			assemblies[4] = typeof(Bindings).Assembly; // Runtime scripts
			assemblies[5] = typeof(GenerateBindings).Assembly; // Editor scripts
			assemblies[6] = typeof(EditorPrefs).Assembly; // UnityEditor
#if UNITY_2017_2_OR_NEWER
			assemblies[7] = typeof(UnityEngine.Accessibility.VisionUtility).Assembly; // Unity accessibility module
			assemblies[8] = typeof(UnityEngine.AI.NavMesh).Assembly; // Unity AI module
			assemblies[9] = typeof(UnityEngine.Animations.AnimationClipPlayable).Assembly; // Unity animation module
			assemblies[10] = typeof(UnityEngine.XR.ARRenderMode).Assembly; // Unity AR module
			assemblies[11] = typeof(UnityEngine.AudioSettings).Assembly; // Unity audio module
			assemblies[12] = typeof(UnityEngine.Cloth).Assembly; // Unity cloth module
			assemblies[13] = typeof(UnityEngine.ClusterInput).Assembly; // Unity cluster input module
			assemblies[14] = typeof(UnityEngine.ClusterNetwork).Assembly; // Unity custer renderer module
			assemblies[15] = typeof(UnityEngine.CrashReportHandler.CrashReportHandler).Assembly; // Unity crash reporting module
			assemblies[16] = typeof(UnityEngine.Playables.PlayableDirector).Assembly; // Unity director module
			assemblies[17] = typeof(UnityEngine.SocialPlatforms.IAchievement).Assembly; // Unity game center module
			assemblies[18] = typeof(UnityEngine.ImageConversion).Assembly; // Unity image conversion module
			assemblies[19] = typeof(UnityEngine.GUI).Assembly; // Unity IMGUI module
			assemblies[20] = typeof(UnityEngine.JsonUtility).Assembly; // Unity JSON serialize module
			assemblies[21] = typeof(UnityEngine.ParticleSystem).Assembly; // Unity particle system module
			assemblies[22] = typeof(UnityEngine.Analytics.PerformanceReporting).Assembly; // Unity performance reporting module
			assemblies[23] = typeof(UnityEngine.Physics2D).Assembly; // Unity physics 2D module
			assemblies[24] = typeof(UnityEngine.Physics).Assembly; // Unity physics module
			assemblies[25] = typeof(UnityEngine.ScreenCapture).Assembly; // Unity screen capture module
			assemblies[26] = typeof(UnityEngine.Terrain).Assembly; // Unity terrain module
			assemblies[27] = typeof(UnityEngine.TerrainCollider).Assembly; // Unity terrain physics module
			assemblies[28] = typeof(UnityEngine.Font).Assembly; // Unity text rendering module
			assemblies[29] = typeof(UnityEngine.Tilemaps.Tile).Assembly; // Unity tilemap module
			assemblies[30] = typeof(UnityEngine.Experimental.UIElements.Button).Assembly; // Unity UI elements module
			assemblies[31] = typeof(UnityEngine.Canvas).Assembly; // Unity UI module
			assemblies[32] = typeof(UnityEngine.Networking.NetworkTransport).Assembly; // Unity cloth module
			assemblies[33] = typeof(UnityEngine.Analytics.Analytics).Assembly; // Unity analytics module
			assemblies[34] = typeof(UnityEngine.RemoteSettings).Assembly; // Unity Unity connect module
			assemblies[35] = typeof(UnityEngine.Networking.DownloadHandlerAudioClip).Assembly; // Unity web request audio module
			assemblies[36] = typeof(UnityEngine.WWWForm).Assembly; // Unity web request module
			assemblies[37] = typeof(UnityEngine.Networking.DownloadHandlerTexture).Assembly; // Unity web request texture module
			assemblies[38] = typeof(UnityEngine.WWW).Assembly; // Unity web request WWW module
			assemblies[39] = typeof(UnityEngine.WheelCollider).Assembly; // Unity vehicles module
			assemblies[40] = typeof(UnityEngine.Video.VideoClip).Assembly; // Unity video module
			assemblies[41] = typeof(UnityEngine.XR.InputTracking).Assembly; // Unity VR module
			assemblies[42] = typeof(UnityEngine.WindZone).Assembly; // Unity wind module
#endif
			return assemblies;
		}
		
		static Type[] GetTypes(
			string[] typeNames,
			Assembly[] assemblies)
		{
			if (typeNames == null)
			{
				return new Type[0];
			}
			Type[] types = new Type[typeNames.Length];
			for (int i = 0; i < typeNames.Length; ++i)
			{
				types[i] = GetType(typeNames[i], assemblies);
			}
			return types;
		}
		
		static Type GetType(
			string typeName,
			Assembly[] assemblies)
		{
			Type type = TryGetType(
				typeName,
				assemblies);
			if (type != null)
			{
				return type;
			}
			
			// Not finding a type is a fatal error
			StringBuilder errorBuilder = new StringBuilder(1024);
			errorBuilder.Append("Couldn't find type \"");
			errorBuilder.Append(typeName);
			errorBuilder.Append('"');
			throw new Exception(errorBuilder.ToString());
		}
		
		static Type TryGetType(
			string typeName,
			Assembly[] assemblies)
		{
			// Search all assemblies for the type
			foreach (Assembly assembly in assemblies)
			{
				Type type = assembly.GetType(typeName);
				if (type != null)
				{
					return type;
				}
			}
			return null;
		}
		
		static TypeKind GetTypeKind(Type type)
		{
			if (type == typeof(void))
			{
				return TypeKind.None;
			}
			
			if (type.IsPointer)
			{
				return TypeKind.Pointer;
			}
			
			if (type.IsEnum)
			{
				return TypeKind.Enum;
			}
			
			if (type.IsPrimitive)
			{
				return TypeKind.Primitive;
			}
			
			if (!type.IsValueType)
			{
				return TypeKind.Class;
			}
			
			// Decimal (currently) can't be represented on the C++ side, so
			// don't count it as a full struct
			if (type != typeof(decimal) && IsFullValueType(type))
			{
				return TypeKind.FullStruct;
			}
			
			return TypeKind.ManagedStruct;
		}
		
		static ParameterInfo[] GetConstructorParameters(
			Type type,
			string[] paramTypeNames)
		{
			foreach (ConstructorInfo ctor in type.GetConstructors())
			{
				System.Reflection.ParameterInfo[] reflectionParams
					= ctor.GetParameters();
				if (CheckParametersMatch(
					paramTypeNames,
					reflectionParams))
				{
					return ConvertParameters(reflectionParams);
				}
			}
			
			// Throw an exception so the user knows what to fix in the JSON
			StringBuilder errorBuilder = new StringBuilder(1024);
			errorBuilder.Append("Constructor \"");
			AppendCsharpTypeName(type, errorBuilder);
			errorBuilder.Append('(');
			for (int i = 0; i < paramTypeNames.Length; ++i)
			{
				errorBuilder.Append(paramTypeNames[i]);
				if (i != paramTypeNames.Length - 1)
				{
					errorBuilder.Append(", ");
				}
			}
			errorBuilder.Append(")\" not found");
			throw new Exception(errorBuilder.ToString());
		}
		
		static MethodInfo GetMethod(
			Type type,
			MethodInfo[] methods,
			string methodName,
			string[] paramTypeNames)
		{
			foreach (MethodInfo method in methods)
			{
				// Name must match
				if (method.Name != methodName)
				{
					continue;
				}
				
				// All parameters must match
				if (CheckParametersMatch(
					paramTypeNames,
					method.GetParameters()))
				{
					return method;
				}
			}
			
			// Throw an exception so the user knows what to fix in the JSON
			StringBuilder errorBuilder = new StringBuilder(1024);
			errorBuilder.Append("Method \"");
			AppendCsharpTypeName(type, errorBuilder);
			errorBuilder.Append('.');
			errorBuilder.Append(methodName);
			errorBuilder.Append('(');
			for (int i = 0; i < paramTypeNames.Length; ++i)
			{
				errorBuilder.Append(paramTypeNames[i]);
				if (i != paramTypeNames.Length - 1)
				{
					errorBuilder.Append(", ");
				}
			}
			errorBuilder.Append(")\" not found");
			throw new Exception(errorBuilder.ToString());
		}
		
		static bool CheckParametersMatch(
			string[] paramTypeNames,
			System.Reflection.ParameterInfo[] reflectionParams)
		{
			// Length must match
			if (reflectionParams.Length != paramTypeNames.Length)
			{
				return false;
			}
			
			// All params must match
			for (int i = 0; i < reflectionParams.Length; ++i)
			{
				Type type = DereferenceParameterType(
					reflectionParams[i]);
				string typeName = paramTypeNames[i];
				if (!CheckTypeNameMatches(typeName, type))
				{
					return false;
				}
			}
			
			return true;
		}
		
		static bool CheckTypeNameMatches(
			string typeName,
			Type type)
		{
			// No namespace. Only name must match.
			if (string.IsNullOrEmpty(type.Namespace))
			{
				if (type.Name != typeName)
				{
					return false;
				}
			}
			// Must be: Namespace.Name
			else
			{
				// Length must be the same as (namespace + '.' + name)
				if (
					typeName.Length !=
					type.Namespace.Length
						+ 1
						+ type.Name.Length)
				{
					return false;
				}
				
				// Must start with namespace
				if (!typeName.StartsWith(type.Namespace))
				{
					return false;
				}
				
				// Namespace must be followed by '.'
				if (typeName[type.Namespace.Length] != '.')
				{
					return false;
				}
				
				// Must end with name
				if (!typeName.EndsWith(type.Name))
				{
					return false;
				}
			}
			
			return true;
		}
		
		static void AppendParameterTypeNames(
			ParameterInfo[] parameters,
			StringBuilder output)
		{
			for (int i = 0, len = parameters.Length; i < len; ++i)
			{
				Type type = parameters[i].DereferencedParameterType;
				AppendNamespace(type.Namespace, string.Empty, output);
				AppendTypeNameWithoutSuffixes(
					type.Name,
					output);
				if (i != len - 1)
				{
					output.Append('_');
				}
			}
		}
		
		static void AppendTypeNames(
			Type[] typeParams,
			StringBuilder output)
		{
			if (typeParams != null)
			{
				for (int i = 0, len = typeParams.Length; i < len; ++i)
				{
					Type curType = typeParams[i];
					AppendNamespace(
						curType.Namespace,
						string.Empty,
						output);
					AppendTypeNameWithoutSuffixes(
						curType.Name,
						output);
					if (i != len - 1)
					{
						output.Append('_');
					}
				}
			}
		}
		
		static void AppendNamespace(
			string namespaceName,
			string separator,
			StringBuilder output)
		{
			int startIndex = 0;
			if (!string.IsNullOrEmpty(namespaceName))
			{
				do
				{
					int separatorIndex = namespaceName.IndexOf(
						'.',
						startIndex);
					if (separatorIndex < 0)
					{
						separatorIndex = namespaceName.IndexOf(
							'+',
							startIndex);
						if (separatorIndex < 0)
						{
							break;
						}
					}
					output.Append(
						namespaceName,
						startIndex,
						separatorIndex - startIndex);
					output.Append(separator);
					startIndex = separatorIndex + 1;
				}
				while (true);
				output.Append(
					namespaceName,
					startIndex,
					namespaceName.Length - startIndex);
			}
		}
		
		static ParameterInfo[] ConvertParameters(
			System.Reflection.ParameterInfo[] reflectionParameters)
		{
			int num = reflectionParameters.Length;
			ParameterInfo[] parameters = new ParameterInfo[num];
			for (int i = 0; i < num; ++i)
			{
				var reflectionInfo = reflectionParameters[i];
				ParameterInfo info = new ParameterInfo();
				info.Name = reflectionInfo.Name;
				info.ParameterType = reflectionInfo.ParameterType;
				info.IsOut = reflectionInfo.IsOut;
				info.IsRef = !info.IsOut && info.ParameterType.IsByRef;
				info.DereferencedParameterType = DereferenceParameterType(
					reflectionInfo);
				info.Kind = GetTypeKind(
					info.DereferencedParameterType);
				parameters[i] = info;
			}
			return parameters;
		}
		
		static Type DereferenceParameterType(
			System.Reflection.ParameterInfo info)
		{
			Type paramType = info.ParameterType;
			return info.IsOut
				? paramType.GetElementType()
				: paramType.IsByRef
					? paramType.GetElementType()
					: paramType;
		}
		
		static ParameterInfo[] ConvertParameters(
			Type[] paramTypes)
		{
			int num = paramTypes.Length;
			ParameterInfo[] parameters = new ParameterInfo[num];
			for (int i = 0; i < num; ++i)
			{
				Type paramType = paramTypes[i];
				ParameterInfo info = new ParameterInfo();
				info.Name = "param" + i;
				info.ParameterType = paramType;
				info.IsOut = false;
				info.IsRef = false;
				info.DereferencedParameterType = paramType;
				info.Kind = GetTypeKind(
					info.DereferencedParameterType);
				parameters[i] = info;
			}
			return parameters;
		}
		
		static bool IsStatic(Type type)
		{
			return type.IsAbstract && type.IsSealed;
		}
		
		static bool IsManagedValueType(Type type)
		{
			return type.IsValueType && !IsFullValueType(type);
		}
		
		static bool IsFullValueType(Type type)
		{
			if (!type.IsValueType)
			{
				return false;
			}
			if (type.IsPrimitive || type.IsEnum)
			{
				return true;
			}
			const BindingFlags bindingFlags =
				BindingFlags.Instance
				| BindingFlags.NonPublic
				| BindingFlags.Public;
			foreach (FieldInfo field in type.GetFields(bindingFlags))
			{
				if (!field.IsStatic
					&& !IsFullValueType(field.FieldType))
				{
					return false;
				}
			}
			return true;
		}
		
		static void AppendTypeNameWithoutGenericSuffix(
			string typeName,
			StringBuilder output)
		{
			// Names are like "List`1"
			// Remove the ` and everything after it
			int backtickIndex = typeName.IndexOf('`');
			if (backtickIndex < 0)
			{
				output.Append(typeName);
			}
			else
			{
				// Append up to (but not including) the `
				output.Append(
					typeName,
					0,
					backtickIndex);
				
				// Find the first non-number after the `
				int endIndex = backtickIndex + 1;
				while (
					endIndex < typeName.Length
					&& char.IsNumber(typeName[endIndex]))
				{
					endIndex++;
				}
				
				// Append everything after the numbers
				if (endIndex < typeName.Length)
				{
					output.Append(
						typeName,
						endIndex,
						typeName.Length - endIndex);
				}
			}
		}
		
		static void AppendTypeNameWithoutSuffixes(
			string typeName,
			StringBuilder output)
		{
			// Names are like "List`1" or "int[]" or "List`1[]"
			// Remove the first of ` or [ and everything after it
			int backtickIndex = typeName.IndexOf('`');
			if (backtickIndex < 0)
			{
				int bracketIndex = typeName.IndexOf('[');
				if (bracketIndex < 0)
				{
					output.Append(typeName);
				}
				else
				{
					output.Append(typeName, 0, bracketIndex);
				}
			}
			else
			{
				output.Append(typeName, 0, backtickIndex);
			}
		}
		
		static void AppendType(
			JsonType jsonType,
			Assembly[] assemblies,
			StringBuilders builders)
		{
			Type type = GetType(jsonType.Name, assemblies);
			TypeKind typeKind = GetTypeKind(type);
			if (typeKind == TypeKind.Enum)
			{
				AppendEnum(
					type,
					builders);
				AppendBoxingUnboxing(
					type,
					typeKind,
					null,
					builders);
			}
			else
			{
				Type[] genericArgTypes = type.GetGenericArguments();
				if (jsonType.GenericParams != null)
				{
					if (!IsStatic(type))
					{
						AppendCppTemplateDeclaration(
							type.Name,
							type.Namespace,
							genericArgTypes.Length,
							builders.CppTypeDeclarations);
					}
					
					foreach (JsonGenericParams jsonGenericParams
						in jsonType.GenericParams)
					{
						Type[] typeParams = GetTypes(
							jsonGenericParams.Types,
							assemblies);
						Type genericType = type.MakeGenericType(typeParams);
						int? maxSimultaneous = jsonGenericParams.MaxSimultaneous != 0
							? jsonGenericParams.MaxSimultaneous
							: jsonType.MaxSimultaneous != 0
								? jsonType.MaxSimultaneous
								: default(int?);
						AppendType(
							jsonType,
							genericArgTypes,
							genericType,
							typeParams,
							maxSimultaneous,
							assemblies,
							builders);
						if (typeKind != TypeKind.Class)
						{
							AppendBoxingUnboxing(
								genericType,
								typeKind,
								typeParams,
								builders);
						}
					}
				}
				else
				{
					int? maxSimultaneous = jsonType.MaxSimultaneous != 0
						? jsonType.MaxSimultaneous
						: default(int?);
					AppendType(
						jsonType,
						genericArgTypes,
						type,
						null,
						maxSimultaneous,
						assemblies,
						builders);
					if (typeKind != TypeKind.Class)
					{
						AppendBoxingUnboxing(
							type,
							typeKind,
							null,
							builders);
					}
				}
			}
		}
		
		static void AppendType(
			JsonType jsonType,
			Type[] genericArgTypes,
			Type type,
			Type[] typeParams,
			int? maxSimultaneous,
			Assembly[] assemblies,
			StringBuilders builders)
		{
			bool isStatic = IsStatic(type);
			TypeKind typeKind = GetTypeKind(type);
			if (!isStatic && typeKind == TypeKind.ManagedStruct)
			{
				// C# StructStore Init call
				builders.CsharpStructStoreInitCalls.Append(
					"\t\t\tNativeScript.Bindings.StructStore<");
				AppendCsharpTypeName(
					type,
					builders.CsharpStructStoreInitCalls);
				builders.CsharpStructStoreInitCalls.Append(
					">.Init(");
				if (maxSimultaneous.HasValue)
				{
					builders.CsharpStructStoreInitCalls.Append(
						maxSimultaneous.Value);
				}
				else
				{
					builders.CsharpStructStoreInitCalls.Append(
						"maxManagedObjects");
				}
				builders.CsharpStructStoreInitCalls.Append(
					");\n");
				
				// Build function name suffix
				builders.TempStrBuilder.Length = 0;
				AppendReleaseFunctionNameSuffix(
					type.Name,
					type.Namespace,
					typeParams,
					builders.TempStrBuilder);
				string funcNameSuffix = builders.TempStrBuilder.ToString();
				
				// Build function name
				builders.TempStrBuilder.Length = 0;
				builders.TempStrBuilder.Append("Release");
				AppendReleaseFunctionNameSuffix(
					type.Name,
					type.Namespace,
					typeParams,
					builders.TempStrBuilder);
				string funcName = builders.TempStrBuilder.ToString();
				
				// Build lowercase function name
				builders.TempStrBuilder[0] = char.ToLower(
					builders.TempStrBuilder[0]);
				string funcNameLower = builders.TempStrBuilder.ToString();
				
				// Build ReleaseX parameters
				ParameterInfo paramInfo = new ParameterInfo();
				paramInfo.Name = "handle";
				paramInfo.ParameterType = typeof(int);
				paramInfo.IsOut = false;
				paramInfo.IsRef = false;
				paramInfo.DereferencedParameterType = typeof(int);
				paramInfo.Kind = TypeKind.Primitive;
				ParameterInfo[] parameters = { paramInfo };
				
				// ReleaseX C# delegate type
				AppendCsharpDelegateType(
					funcName,
					true,
					type,
					typeKind,
					typeof(void),
					parameters,
					builders.CsharpDelegateTypes);
				
				// ReleaseX C# function
				AppendCsharpFunctionBeginning(
					type,
					funcName,
					true,
					typeKind,
					typeof(void),
					parameters,
					builders.CsharpFunctions);
				builders.CsharpFunctions.Append(
					"if (handle != 0)\n\t\t\t{\n");
				builders.CsharpFunctions.Append(
					"\t\t\t\tNativeScript.Bindings.StructStore<");
				AppendCsharpTypeName(
					type,
					builders.CsharpFunctions);
				builders.CsharpFunctions.Append(
					">.Remove(handle);\n\t\t\t}");
				AppendCsharpFunctionEnd(
					typeof(void),
					new Type[0],
					builders.CsharpFunctions);
				
				// C++ function pointer definition
				AppendCppFunctionPointerDefinition(
					funcName,
					true,
					null,
					null,
					TypeKind.None,
					parameters,
					typeof(void),
					builders.CppFunctionPointers);
				
				// C++ init param for ReleaseX
				AppendCppInitParam(
					funcNameLower,
					true,
					null,
					null,
					TypeKind.None,
					parameters,
					typeof(void),
					builders.CppInitParams);
				
				// C++ init body for ReleaseX
				AppendCppInitBody(
					funcName,
					funcNameLower,
					builders.CppInitBody);
				
				// C# init param for ReleaseX
				AppendCsharpInitParam(
					funcNameLower,
					builders.CsharpInitParams);
				
				// C# init call arg for ReleaseX
				AppendCsharpInitCallArg(
					funcName,
					builders.CsharpInitCall);
				
				// C++ init body for handle array length
				builders.CppInitBody.Append("\tPlugin::RefCounts");
				builders.CppInitBody.Append(funcNameSuffix);
				builders.CppInitBody.Append(" = new int32_t[");
				if (maxSimultaneous.HasValue)
				{
					builders.CppInitBody.Append(maxSimultaneous.Value);
				}
				else
				{
					builders.CppInitBody.Append("maxManagedObjects");
				}
				builders.CppInitBody.Append("]();\n");
				
				// C++ ref count state and functions
				builders.CppGlobalStateAndFunctions.Append("\tint32_t RefCountsLen");
				builders.CppGlobalStateAndFunctions.Append(funcNameSuffix);
				builders.CppGlobalStateAndFunctions.Append(";\n\tint32_t* RefCounts");
				builders.CppGlobalStateAndFunctions.Append(funcNameSuffix);
				builders.CppGlobalStateAndFunctions.Append(";\n\t\n\tvoid ReferenceManaged");
				builders.CppGlobalStateAndFunctions.Append(funcNameSuffix);
				builders.CppGlobalStateAndFunctions.Append("(int32_t handle)\n");
				builders.CppGlobalStateAndFunctions.Append("\t{\n");
				builders.CppGlobalStateAndFunctions.Append("\t\tassert(handle >= 0 && handle < RefCountsLen");
				builders.CppGlobalStateAndFunctions.Append(funcNameSuffix);
				builders.CppGlobalStateAndFunctions.Append(");\n");
				builders.CppGlobalStateAndFunctions.Append("\t\tif (handle != 0)\n");
				builders.CppGlobalStateAndFunctions.Append("\t\t{\n");
				builders.CppGlobalStateAndFunctions.Append("\t\t\tRefCounts");
				builders.CppGlobalStateAndFunctions.Append(funcNameSuffix);
				builders.CppGlobalStateAndFunctions.Append("[handle]++;\n");
				builders.CppGlobalStateAndFunctions.Append("\t\t}\n");
				builders.CppGlobalStateAndFunctions.Append("\t}\n");
				builders.CppGlobalStateAndFunctions.Append("\t\n");
				builders.CppGlobalStateAndFunctions.Append("\tvoid DereferenceManaged");
				builders.CppGlobalStateAndFunctions.Append(funcNameSuffix);
				builders.CppGlobalStateAndFunctions.Append("(int32_t handle)\n");
				builders.CppGlobalStateAndFunctions.Append("\t{\n");
				builders.CppGlobalStateAndFunctions.Append("\t\tassert(handle >= 0 && handle < RefCountsLen");
				builders.CppGlobalStateAndFunctions.Append(funcNameSuffix);
				builders.CppGlobalStateAndFunctions.Append(");\n");
				builders.CppGlobalStateAndFunctions.Append("\t\tif (handle != 0)\n");
				builders.CppGlobalStateAndFunctions.Append("\t\t{\n");
				builders.CppGlobalStateAndFunctions.Append("\t\t\tint32_t numRemain = --RefCounts");
				builders.CppGlobalStateAndFunctions.Append(funcNameSuffix);
				builders.CppGlobalStateAndFunctions.Append("[handle];\n");
				builders.CppGlobalStateAndFunctions.Append("\t\t\tif (numRemain == 0)\n");
				builders.CppGlobalStateAndFunctions.Append("\t\t\t{\n");
				builders.CppGlobalStateAndFunctions.Append("\t\t\t\tRelease");
				builders.CppGlobalStateAndFunctions.Append(funcNameSuffix);
				builders.CppGlobalStateAndFunctions.Append("(handle);\n");
				builders.CppGlobalStateAndFunctions.Append("\t\t\t}\n");
				builders.CppGlobalStateAndFunctions.Append("\t\t}\n");
				builders.CppGlobalStateAndFunctions.Append("\t}\n\t\n");
			}
			
			// C++ type declaration
			int indent = AppendCppTypeDeclaration(
				type.Namespace,
				type.Name,
				isStatic,
				typeParams,
				builders.CppTypeDeclarations);
			
			// C++ type definition (beginning)
			Type baseType = type.BaseType ?? typeof(object);
			AppendCppTypeDefinitionBegin(
				type.Name,
				type.Namespace,
				typeKind,
				typeParams,
				baseType.Name,
				baseType.Namespace,
				baseType.GetGenericArguments(),
				isStatic,
				indent,
				builders.CppTypeDefinitions);
			
			// C++ method definition
			int cppMethodDefinitionsIndent = AppendCppMethodDefinitionsBegin(
				type.Name,
				type.Namespace,
				typeKind,
				typeParams,
				baseType.Name,
				baseType.Namespace,
				baseType.GetGenericArguments(),
				isStatic,
				(extraIndent, subject) => {},
				(extraIndent, subject) => {},
				indent,
				builders.CppMethodDefinitions);
			
			// Constructors
			if (typeKind == TypeKind.FullStruct)
			{
				AppendFullValueTypeDefaultConstructor(
					type,
					indent,
					builders);
			}
			if (jsonType.Constructors != null)
			{
				foreach (JsonConstructor jsonCtor in jsonType.Constructors)
				{
					AppendConstructor(
						jsonCtor.ParamTypes,
						jsonCtor.Exceptions,
						type,
						isStatic,
						typeKind,
						assemblies,
						typeParams,
						genericArgTypes,
						indent,
						builders);
				}
			}
			
			// Properties
			if (jsonType.Properties != null)
			{
				foreach (JsonProperty jsonProperty in jsonType.Properties)
				{
					AppendProperty(
						jsonProperty,
						type,
						isStatic,
						typeKind,
						typeParams,
						genericArgTypes,
						indent,
						assemblies,
						builders);
				}
			}
			
			// Fields
			if (typeKind == TypeKind.FullStruct)
			{
				AppendFullValueTypeFields(
					type,
					indent + 1,
					builders);
			}
			else
			{
				if (jsonType.Fields != null)
				{
					foreach (string jsonFieldName in jsonType.Fields)
					{
						AppendField(
							jsonFieldName,
							type,
							isStatic,
							typeKind,
							typeParams,
							genericArgTypes,
							indent,
							builders
						);
					}
				}
			}
			
			if (jsonType.Events != null)
			{
				foreach (JsonEvent jsonEvent in jsonType.Events)
				{
					AppendEvent(
						jsonEvent,
						type,
						isStatic,
						typeKind,
						typeParams,
						indent,
						builders
					);
				}
			}
			
			// Methods
			if (jsonType.Methods != null)
			{
				MethodInfo[] methods = type.GetMethods();
				foreach (JsonMethod jsonMethod in jsonType.Methods)
				{
					AppendMethod(
						jsonMethod,
						assemblies,
						type,
						isStatic,
						typeKind,
						methods,
						typeParams,
						genericArgTypes,
						indent,
						builders);
				}
			}
			
			// C++ type definition (ending)
			AppendCppTypeDefinitionEnd(
				isStatic,
				indent,
				builders.CppTypeDefinitions);
			
			// C++ method definition (ending)
			AppendCppMethodDefinitionsEnd(
				cppMethodDefinitionsIndent,
				builders.CppMethodDefinitions);
		}
		
		static void AppendBaseType(
			JsonBaseType jsonBaseType,
			Assembly[] assemblies,
			StringBuilders builders)
		{
			Type type = GetType(jsonBaseType.Name, assemblies);
			Type[] genericArgTypes = type.GetGenericArguments();
			if (jsonBaseType.GenericParams != null)
			{
				if (!IsStatic(type))
				{
					AppendCppTemplateDeclaration(
						type.Name,
						type.Namespace,
						genericArgTypes.Length,
						builders.CppTypeDeclarations);
				}
				
				foreach (JsonGenericParams jsonGenericParams
					in jsonBaseType.GenericParams)
				{
					Type[] typeParams = GetTypes(
						jsonGenericParams.Types,
						assemblies);
					Type genericType = type.MakeGenericType(typeParams);
					int? maxSimultaneous = jsonGenericParams.MaxSimultaneous != 0
						? jsonGenericParams.MaxSimultaneous
						: jsonBaseType.MaxSimultaneous != 0
							? jsonBaseType.MaxSimultaneous
							: default(int?);
					AppendBaseType(
						genericType,
						jsonBaseType,
						type.Name,
						typeParams,
						maxSimultaneous,
						builders);
				}
			}
			else
			{
				int? maxSimultaneous = jsonBaseType.MaxSimultaneous != 0
					? jsonBaseType.MaxSimultaneous
					: default(int?);
				AppendBaseType(
					type,
					jsonBaseType,
					type.Name,
					null,
					maxSimultaneous,
					builders);
			}
		}
		
		static void AppendReleaseFunctionNameSuffix(
			string typeName,
			string typeNamespace,
			Type[] typeParams,
			StringBuilder output)
		{
			AppendNamespace(
				typeNamespace,
				string.Empty,
				output);
			AppendTypeNameWithoutSuffixes(
				typeName,
				output);
			if (typeParams != null)
			{
				for (int i = 0, len = typeParams.Length; i < len; ++i)
				{
					Type typeParam = typeParams[i];
					AppendNamespace(
						typeParam.Namespace,
						string.Empty,
						output);
					AppendTypeNameWithoutSuffixes(
						typeParam.Name,
						output);
					if (i != len - 1)
					{
						output.Append('_');
					}
				}
			}
		}
		
		static void AppendEnum(
			Type type,
			StringBuilders builders)
		{
			// C++ type declaration (actually definition)
			int indent = AppendNamespaceBeginning(
				type.Namespace,
				builders.CppTypeDeclarations);
			AppendIndent(
				indent,
				builders.CppTypeDeclarations);
			builders.CppTypeDeclarations.Append("enum struct ");
			builders.CppTypeDeclarations.Append(type.Name);
			builders.CppTypeDeclarations.Append(" : ");
			AppendCppTypeName(
				Enum.GetUnderlyingType(type),
				builders.CppTypeDeclarations);
			builders.CppTypeDeclarations.Append('\n');
			AppendIndent(
				indent,
				builders.CppTypeDeclarations);
			builders.CppTypeDeclarations.Append("{\n");
			FieldInfo[] fields = type.GetFields(
				BindingFlags.Static
				| BindingFlags.Public);
			for (int i = 0; i < fields.Length; ++i)
			{
				FieldInfo field = fields[i];
				AppendIndent(
					indent + 1,
					builders.CppTypeDeclarations);
				builders.CppTypeDeclarations.Append(field.Name);
				builders.CppTypeDeclarations.Append(" = ");
				builders.CppTypeDeclarations.Append(
					field.GetRawConstantValue());
				if (i != fields.Length - 1)
				{
					builders.CppTypeDeclarations.Append(',');
				}
				builders.CppTypeDeclarations.Append('\n');
			}
			AppendIndent(
				indent,
				builders.CppTypeDeclarations);
			builders.CppTypeDeclarations.Append("};\n");
			AppendNamespaceEnding(
				indent,
				builders.CppTypeDeclarations);
			builders.CppTypeDeclarations.Append('\n');
		}
		
		static void AppendBoxingUnboxing(
			Type type,
			TypeKind typeKind,
			Type[] typeParams,
			StringBuilders builders)
		{
			builders.TempStrBuilder.Length = 0;
			builders.TempStrBuilder.Append("Box");
			AppendTypeNameWithoutSuffixes(
				type.Name,
				builders.TempStrBuilder);
			AppendTypeNames(
				typeParams,
				builders.TempStrBuilder);
			string boxFuncName = builders.TempStrBuilder.ToString();
			
			builders.TempStrBuilder[0] = char.ToLower(
				builders.TempStrBuilder[0]);
			string boxFuncNameLower = builders.TempStrBuilder.ToString();
			
			builders.TempStrBuilder.Length = 0;
			builders.TempStrBuilder.Append("Unbox");
			AppendTypeNameWithoutSuffixes(
				type.Name,
				builders.TempStrBuilder);
			AppendTypeNames(
				typeParams,
				builders.TempStrBuilder);
			string unboxFuncName = builders.TempStrBuilder.ToString();
			
			builders.TempStrBuilder[0] = char.ToLower(
				builders.TempStrBuilder[0]);
			string unboxFuncNameLower = builders.TempStrBuilder.ToString();
			
			builders.TempStrBuilder.Length = 0;
			builders.TempStrBuilder.Append("operator ");
			AppendCppTypeName(
				type,
				builders.TempStrBuilder);
			string unboxMethodDefinitionName = builders.TempStrBuilder.ToString();
			
			builders.TempStrBuilder.Length = 0;
			builders.TempStrBuilder.Append("explicit ");
			builders.TempStrBuilder.Append(unboxMethodDefinitionName);
			string unboxMethodDeclarationName = builders.TempStrBuilder.ToString();
			
			ParameterInfo[] boxParams = {
				new ParameterInfo
				{
					Name = "val",
					ParameterType = type,
					DereferencedParameterType = type,
					IsOut = false,
					IsRef = false,
					Kind = typeKind
				}
			};
			
			ParameterInfo[] unboxParams = {
				new ParameterInfo
				{
					Name = "val",
					ParameterType = typeof(object),
					DereferencedParameterType = typeof(object),
					IsOut = false,
					IsRef = false,
					Kind = TypeKind.Class
				}
			};
			
			ParameterInfo[] unboxCppParams = new ParameterInfo[0];
			
			// C# init params
			AppendCsharpInitParam(
				boxFuncNameLower,
				builders.CsharpInitParams);
			AppendCsharpInitParam(
				unboxFuncNameLower,
				builders.CsharpInitParams);
			
			// C# delegate types
			AppendCsharpDelegateType(
				boxFuncName,
				true,
				type,
				typeKind,
				typeof(object),
				boxParams,
				builders.CsharpDelegateTypes);
			AppendCsharpDelegateType(
				unboxFuncName,
				true,
				type,
				typeKind,
				type,
				unboxParams,
				builders.CsharpDelegateTypes);
			
			// C# init call args
			AppendCsharpInitCallArg(
				boxFuncName,
				builders.CsharpInitCall);
			AppendCsharpInitCallArg(
				unboxFuncName,
				builders.CsharpInitCall);
			
			// C# box function
			AppendCsharpFunctionBeginning(
				typeof(object),
				boxFuncName,
				true,
				TypeKind.Class,
				typeof(object),
				boxParams,
				builders.CsharpFunctions);
			builders.CsharpFunctions.Append(
				"NativeScript.Bindings.ObjectStore.Store((object)val);");
			AppendCsharpFunctionReturn(
				boxParams,
				typeof(object),
				TypeKind.Class,
				null,
				true,
				builders.CsharpFunctions);
			
			// C# unbox function
			AppendCsharpFunctionBeginning(
				typeof(object),
				unboxFuncName,
				true,
				TypeKind.Class,
				type,
				unboxParams,
				builders.CsharpFunctions);
			switch (typeKind)
			{
				case TypeKind.Class:
				case TypeKind.ManagedStruct:
					AppendHandleStoreTypeName(
						type,
						builders.CsharpFunctions);
					builders.CsharpFunctions.Append(".Store((");
					AppendCsharpTypeName(
						type,
						builders.CsharpFunctions);
					builders.CsharpFunctions.Append(")val);");
					break;
				default:
					builders.CsharpFunctions.Append('(');
					AppendCsharpTypeName(
						type,
						builders.CsharpFunctions);
					builders.CsharpFunctions.Append(")val;");
					break;
			}
			AppendCsharpFunctionReturn(
				unboxParams,
				type,
				typeKind,
				null,
				true,
				builders.CsharpFunctions);
			
			// C++ function pointers
			AppendCppFunctionPointerDefinition(
				boxFuncName,
				true,
				type.Name,
				type.Namespace,
				typeKind,
				boxParams,
				typeof(object),
				builders.CppFunctionPointers);
			AppendCppFunctionPointerDefinition(
				unboxFuncName,
				true,
				type.Name,
				type.Namespace,
				typeKind,
				unboxParams,
				type,
				builders.CppFunctionPointers);
			
			// C++ method declarations
			AppendIndent(
				2,
				builders.CppBoxingMethodDeclarations);
			AppendCppMethodDeclaration(
				"Object",
				false,
				false,
				false,
				null,
				null,
				boxParams,
				builders.CppBoxingMethodDeclarations);
			AppendIndent(
				2,
				builders.CppBoxingMethodDeclarations);
			AppendCppMethodDeclaration(
				unboxMethodDeclarationName,
				false,
				false,
				false,
				null,
				null,
				unboxCppParams,
				builders.CppBoxingMethodDeclarations);
			
			// C++ method definitions (begin)
			int indent = AppendNamespaceBeginning(
				"System",
				builders.CppMethodDefinitions);
			
			// C++ box method definition
			AppendCppMethodDefinitionBegin(
				"Object",
				null,
				"Object",
				null,
				null,
				boxParams,
				indent,
				builders.CppMethodDefinitions);
			AppendIndent(
				indent,
				builders.CppMethodDefinitions);
			builders.CppMethodDefinitions.Append("{\n");
			AppendIndent(
				indent + 1,
				builders.CppMethodDefinitions);
			builders.CppMethodDefinitions.Append("int32_t handle = Plugin::");
			builders.CppMethodDefinitions.Append(boxFuncName);
			builders.CppMethodDefinitions.Append("(val");
			if (typeKind == TypeKind.ManagedStruct)
			{
				builders.CppMethodDefinitions.Append(".Handle");
			}
			builders.CppMethodDefinitions.Append(");\n");
			AppendCppUnhandledExceptionHandling(
				indent + 1,
				builders.CppMethodDefinitions);
			AppendIndent(
				indent + 1,
				builders.CppMethodDefinitions);
			builders.CppMethodDefinitions.Append(
				"if (handle)\n");
			AppendIndent(
				indent + 1,
				builders.CppMethodDefinitions);
			builders.CppMethodDefinitions.Append(
				"{\n");
			AppendIndent(
				indent + 2,
				builders.CppMethodDefinitions);
			AppendReferenceManagedHandleFunctionCall(
				"Object",
				"System",
				TypeKind.Class,
				null,
				"handle",
				builders.CppMethodDefinitions);
			builders.CppMethodDefinitions.Append(";\n");
			AppendIndent(
				indent + 2,
				builders.CppMethodDefinitions);
			builders.CppMethodDefinitions.Append("Handle = handle;\n");
			AppendIndent(
				indent + 1,
				builders.CppMethodDefinitions);
			builders.CppMethodDefinitions.Append(
				"}\n");
			AppendIndent(
				indent,
				builders.CppMethodDefinitions);
			builders.CppMethodDefinitions.Append("}\n\t\n");
			
			// C++ unbox method definition
			AppendCppMethodDefinitionBegin(
				"Object",
				null,
				unboxMethodDefinitionName,
				null,
				null,
				unboxCppParams,
				indent,
				builders.CppMethodDefinitions);
			AppendIndent(
				indent,
				builders.CppMethodDefinitions);
			builders.CppMethodDefinitions.Append("{\n");
			AppendIndent(
				indent + 1,
				builders.CppMethodDefinitions);
			AppendCppTypeName(
				type,
				builders.CppMethodDefinitions);
			builders.CppMethodDefinitions.Append(" returnVal(");
			if (typeKind == TypeKind.ManagedStruct)
			{
				builders.CppMethodDefinitions.Append("Plugin::InternalUse::Only, ");
			}
			builders.CppMethodDefinitions.Append("Plugin::");
			builders.CppMethodDefinitions.Append(unboxFuncName);
			builders.CppMethodDefinitions.Append("(Handle));\n");
			AppendCppUnhandledExceptionHandling(
				indent + 1,
				builders.CppMethodDefinitions);
			AppendIndent(
				indent + 1,
				builders.CppMethodDefinitions);
			builders.CppMethodDefinitions.Append("return returnVal;\n");
			AppendIndent(
				indent,
				builders.CppMethodDefinitions);
			builders.CppMethodDefinitions.Append("}\n\t\n");
			
			// C++ method definitions (end)
			AppendCppMethodDefinitionsEnd(
				indent,
				builders.CppMethodDefinitions);
			
			// C++ init params
			AppendCppInitParam(
				boxFuncNameLower,
				true,
				type.Name,
				type.Namespace,
				typeKind,
				boxParams,
				typeof(object),
				builders.CppInitParams);
			AppendCppInitParam(
				unboxFuncNameLower,
				true,
				type.Name,
				type.Namespace,
				typeKind,
				unboxParams,
				type,
				builders.CppInitParams);
			
			// C++ init body
			AppendCppInitBody(
				boxFuncName,
				boxFuncNameLower,
				builders.CppInitBody);
			AppendCppInitBody(
				unboxFuncName,
				unboxFuncNameLower,
				builders.CppInitBody);
		}
		
		static void AppendHandleStoreTypeName(
			Type type,
			StringBuilder output)
		{
			output.Append("NativeScript.Bindings.");
			if (IsManagedValueType(type))
			{
				output.Append("StructStore<");
				AppendCsharpTypeName(type, output);
				output.Append('>');
			}
			else
			{
				output.Append("ObjectStore");
			}
		}
		
		static void AppendConstructor(
			string[] paramTypeNames,
			string[] exceptionNames,
			Type enclosingType,
			bool enclosingTypeIsStatic,
			TypeKind enclosingTypeKind,
			Assembly[] assemblies,
			Type[] enclosingTypeParams,
			Type[] genericArgTypes,
			int indent,
			StringBuilders builders)
		{
			// Get the constructor's parameters
			ParameterInfo[] parameters;
			if (enclosingType.IsValueType
				&& !enclosingType.IsPrimitive
				&& !enclosingType.IsEnum
				&& paramTypeNames.Length == 0)
			{
				// Allow parameterless constructor for structs
				parameters = new ParameterInfo[0];
			}
			else
			{
				string[] constructorParamTypeNames;
				if (enclosingType.IsGenericType)
				{
					constructorParamTypeNames = OverrideGenericTypeNames(
						paramTypeNames,
						genericArgTypes,
						enclosingTypeParams);
				}
				else
				{
					constructorParamTypeNames = paramTypeNames;
				}
				parameters = GetConstructorParameters(
					enclosingType,
					constructorParamTypeNames);
			}
			
			Type[] exceptionTypes = GetTypes(
				exceptionNames,
				assemblies);
			
			// Build uppercase function name
			builders.TempStrBuilder.Length = 0;
			AppendNamespace(
				enclosingType.Namespace,
				string.Empty,
				builders.TempStrBuilder);
			AppendTypeNameWithoutGenericSuffix(
				enclosingType.Name,
				builders.TempStrBuilder);
			AppendTypeNames(
				enclosingTypeParams,
				builders.TempStrBuilder);
			builders.TempStrBuilder.Append("Constructor");
			AppendParameterTypeNames(
				parameters,
				builders.TempStrBuilder);
			string funcName = builders.TempStrBuilder.ToString();
			
			// Build lowercase function name
			builders.TempStrBuilder[0] = char.ToLower(
				builders.TempStrBuilder[0]);
			string funcNameLower = builders.TempStrBuilder.ToString();
			
			// C# init param declaration
			AppendCsharpInitParam(
				funcNameLower,
				builders.CsharpInitParams);

			// C# delegate type
			Type delegateReturnType;
			if (enclosingTypeKind == TypeKind.FullStruct)
			{
				delegateReturnType = enclosingType;
			}
			else
			{
				delegateReturnType = typeof(int);
			}
			AppendCsharpDelegateType(
				funcName,
				true,
				enclosingType,
				enclosingTypeKind,
				delegateReturnType,
				parameters,
				builders.CsharpDelegateTypes);

			// C# init call param
			AppendCsharpInitCallArg(funcName, builders.CsharpInitCall);

			// C# function
			if (enclosingTypeKind == TypeKind.FullStruct)
			{
				AppendCsharpFunctionBeginning(
					enclosingType,
					funcName,
					true,
					enclosingTypeKind,
					enclosingType,
					parameters,
					builders.CsharpFunctions);
				builders.CsharpFunctions.Append("new ");
				AppendCsharpTypeName(
					enclosingType,
					builders.CsharpFunctions);
				AppendCsharpFunctionCallParameters(
					parameters,
					builders.CsharpFunctions);
				builders.CsharpFunctions.Append(";");
				AppendCsharpFunctionReturn(
					parameters,
					enclosingType,
					enclosingTypeKind,
					exceptionTypes,
					true,
					builders.CsharpFunctions);
			}
			else
			{
				AppendCsharpFunctionBeginning(
					enclosingType,
					funcName,
					true,
					enclosingTypeKind,
					typeof(int),
					parameters,
					builders.CsharpFunctions);
				AppendHandleStoreTypeName(
					enclosingType,
					builders.CsharpFunctions);
				builders.CsharpFunctions.Append(
					".Store(new ");
				AppendCsharpTypeName(
					enclosingType,
					builders.CsharpFunctions);
				AppendCsharpFunctionCallParameters(
					parameters,
					builders.CsharpFunctions);
				builders.CsharpFunctions.Append(");");
				AppendCsharpFunctionReturn(
					parameters,
					typeof(int),
					TypeKind.Primitive,
					exceptionTypes,
					true,
					builders.CsharpFunctions);
			}
			
			// C++ function pointer
			AppendCppFunctionPointerDefinition(
				funcName,
				true,
				enclosingType.Name,
				enclosingType.Namespace,
				enclosingTypeKind,
				parameters,
				enclosingType,
				builders.CppFunctionPointers);
			
			// C++ type declaration
			AppendIndent(
				indent + 1,
				builders.CppTypeDefinitions);
			AppendCppMethodDeclaration(
				enclosingType.Name,
				enclosingTypeIsStatic,
				false,
				false,
				null,
				null,
				parameters,
				builders.CppTypeDefinitions);
			
			// C++ method definition
			AppendCppMethodDefinitionBegin(
				enclosingType.Name,
				null,
				enclosingType.Name,
				enclosingTypeParams,
				null,
				parameters,
				indent,
				builders.CppMethodDefinitions);
			if (enclosingTypeKind != TypeKind.FullStruct)
			{
				AppendIndent(
					indent + 1,
					builders.CppMethodDefinitions);
				builders.CppMethodDefinitions.Append(" : ");
				AppendCppTypeName(
					enclosingType.BaseType,
					builders.CppMethodDefinitions);
				builders.CppMethodDefinitions.Append("(nullptr)\n");
			}
			AppendIndent(
				indent,
				builders.CppMethodDefinitions);
			builders.CppMethodDefinitions.Append("{\n");
			AppendCppPluginFunctionCall(
				true,
				enclosingType.Name,
				enclosingType.Namespace,
				enclosingTypeKind,
				enclosingTypeParams,
				enclosingType,
				funcName,
				parameters,
				indent + 1,
				builders.CppMethodDefinitions);
			if (enclosingTypeKind == TypeKind.FullStruct)
			{
				AppendIndent(
					indent + 1,
					builders.CppMethodDefinitions);
				builders.CppMethodDefinitions.Append(
					"*this = returnValue;\n");
			}
			else
			{
				AppendIndent(
					indent + 1,
					builders.CppMethodDefinitions);
				builders.CppMethodDefinitions.Append(
					"Handle = returnValue;\n");
				AppendIndent(
					indent + 1,
					builders.CppMethodDefinitions);
				builders.CppMethodDefinitions.Append(
					"if (returnValue)\n");
				AppendIndent(
					indent + 1,
					builders.CppMethodDefinitions);
				builders.CppMethodDefinitions.Append(
					"{\n");
				AppendIndent(
					indent + 2,
					builders.CppMethodDefinitions);
				AppendReferenceManagedHandleFunctionCall(
					enclosingType.Name,
					enclosingType.Namespace,
					enclosingTypeKind,
					enclosingTypeParams,
					"returnValue",
					builders.CppMethodDefinitions);
				builders.CppMethodDefinitions.Append(";\n");
				AppendIndent(
					indent + 1,
					builders.CppMethodDefinitions);
				builders.CppMethodDefinitions.Append(
					"}\n");
			}
			AppendIndent(
				indent,
				builders.CppMethodDefinitions);
			builders.CppMethodDefinitions.Append("}\n");
			AppendIndent(
				indent,
				builders.CppMethodDefinitions);
			builders.CppMethodDefinitions.Append("\n");

			// C++ init params
			AppendCppInitParam(
				funcNameLower,
				true,
				enclosingType.Name,
				enclosingType.Namespace,
				enclosingTypeKind,
				parameters,
				enclosingType,
				builders.CppInitParams);
			
			// C++ init body
			AppendCppInitBody(
				funcName,
				funcNameLower,
				builders.CppInitBody);
		}
		
		static void AppendProperty(
			JsonProperty jsonProperty,
			Type enclosingType,
			bool enclosingTypeIsStatic,
			TypeKind enclosingTypeKind,
			Type[] typeParams,
			Type[] typeGenericArgumentTypes,
			int indent,
			Assembly[] assemblies,
			StringBuilders builders)
		{
			JsonPropertyGet jsonPropertyGet = jsonProperty.Get;
			if (jsonPropertyGet != null)
			{
				PropertyInfo property = null;
				MethodInfo getMethod;
				if (jsonPropertyGet.ParamTypes != null)
				{
					PropertyInfo[] properties = enclosingType.GetProperties();
					foreach (PropertyInfo curProperty in properties)
					{
						// Name must match
						if (curProperty.Name != jsonProperty.Name)
						{
							continue;
						}
						
						// Must have a get method
						getMethod = curProperty.GetGetMethod();
						if (getMethod == null)
						{
							continue;
						}
						
						// All parameters must match
						if (CheckParametersMatch(
							jsonPropertyGet.ParamTypes,
							getMethod.GetParameters()))
						{
							property = curProperty;
							break;
						}
					}
				}
				else
				{
					property = enclosingType.GetProperty(jsonProperty.Name);
				}
				
				if (property == null)
				{
					builders.TempStrBuilder.Length = 0;
					builders.TempStrBuilder.Append("Property '");
					builders.TempStrBuilder.Append(jsonProperty.Name);
					builders.TempStrBuilder.Append("' not found on ");
					builders.TempStrBuilder.Append(enclosingType);
					throw new Exception(builders.TempStrBuilder.ToString());
				}
				
				getMethod = property.GetGetMethod();
				if (getMethod != null)
				{
					Type propertyType = property.PropertyType;
					TypeKind propertyTypeKind = GetTypeKind(propertyType);
					Type[] exceptionTypes = GetTypes(
						jsonPropertyGet.Exceptions,
						assemblies);
					ParameterInfo[] parameters = ConvertParameters(
						getMethod.GetParameters());
					OverrideGenericParameterTypes(
						parameters,
						typeGenericArgumentTypes,
						typeParams);
					AppendGetter(
						property.Name,
						"Property",
						parameters,
						enclosingTypeIsStatic,
						enclosingTypeKind,
						getMethod.IsStatic,
						jsonPropertyGet.IsReadOnly,
						enclosingType,
						typeParams,
						propertyType,
						propertyTypeKind,
						indent,
						exceptionTypes,
						builders);
				}
			}
			
			JsonPropertySet jsonPropertySet = jsonProperty.Set;
			if (jsonPropertySet != null)
			{
				PropertyInfo property = null;
				if (jsonPropertySet.ParamTypes != null)
				{
					PropertyInfo[] properties = enclosingType.GetProperties();
					foreach (PropertyInfo curProperty in properties)
					{
						// Name must match
						if (curProperty.Name != jsonProperty.Name)
						{
							continue;
						}
						
						// Must have a set method
						MethodInfo setMethod = curProperty.GetSetMethod();
						if (setMethod == null)
						{
							continue;
						}
						
						// All parameters must match
						if (CheckParametersMatch(
							jsonPropertySet.ParamTypes,
							setMethod.GetParameters()))
						{
							property = curProperty;
							break;
						}
					}
				}
				else
				{
					property = enclosingType.GetProperty(jsonProperty.Name);
				}
				
				if (property == null)
				{
					builders.TempStrBuilder.Length = 0;
					builders.TempStrBuilder.Append("Property '");
					builders.TempStrBuilder.Append(jsonProperty.Name);
					builders.TempStrBuilder.Append("' not found on ");
					builders.TempStrBuilder.Append(enclosingType);
					throw new Exception(builders.TempStrBuilder.ToString());
				}
				
				MethodInfo method = property.GetSetMethod();
				if (method != null)
				{
					Type[] exceptionTypes = GetTypes(
						jsonPropertySet.Exceptions,
						assemblies);
					ParameterInfo[] parameters = ConvertParameters(
						method.GetParameters());
					OverrideGenericParameterTypes(
						parameters,
						typeGenericArgumentTypes,
						typeParams);
					AppendSetter(
						property.Name,
						"Property",
						parameters,
						enclosingTypeIsStatic,
						enclosingTypeKind,
						method.IsStatic,
						jsonPropertySet.IsReadOnly,
						enclosingType,
						typeParams,
						indent,
						exceptionTypes,
						builders);
				}
			}
		}
		
		static void AppendFullValueTypeDefaultConstructor(
			Type enclosingType,
			int indent,
			StringBuilders builders)
		{
			AppendIndent(
				indent + 1,
				builders.CppTypeDefinitions);
			AppendTypeNameWithoutGenericSuffix(
				enclosingType.Name,
				builders.CppTypeDefinitions);
			builders.CppTypeDefinitions.Append("();\n");
			
			AppendIndent(
				indent,
				builders.CppMethodDefinitions);
			AppendTypeNameWithoutGenericSuffix(
				enclosingType.Name,
				builders.CppMethodDefinitions);
			builders.CppMethodDefinitions.Append("::");
			AppendTypeNameWithoutGenericSuffix(
				enclosingType.Name,
				builders.CppMethodDefinitions);
			builders.CppMethodDefinitions.Append("()\n");
			AppendIndent(
				indent,
				builders.CppMethodDefinitions);
			builders.CppMethodDefinitions.Append("{\n");
			AppendIndent(
				indent,
				builders.CppMethodDefinitions);
			builders.CppMethodDefinitions.Append("}\n");
			AppendIndent(
				indent,
				builders.CppMethodDefinitions);
			builders.CppMethodDefinitions.Append('\n');
		}
		
		static void AppendFullValueTypeFields(
			Type enclosingType,
			int indent,
			StringBuilders builders)
		{
			FieldInfo[] fields = enclosingType.GetFields(
				BindingFlags.Instance
				| BindingFlags.Public
				| BindingFlags.NonPublic);
			Array.Sort(fields, DefaultFieldOrderComparer);
			foreach (FieldInfo field in fields)
			{
				AppendIndent(
					indent,
					builders.CppTypeDefinitions);
				AppendCppTypeName(
					field.FieldType,
					builders.CppTypeDefinitions);
				builders.CppTypeDefinitions.Append(' ');
				builders.CppTypeDefinitions.Append(field.Name);
				builders.CppTypeDefinitions.Append(";\n");
			}
		}
		
		static void AppendField(
			string jsonFieldName,
			Type enclosingType,
			bool enclosingTypeIsStatic,
			TypeKind enclosingTypeKind,
			Type[] typeTypeParams,
			Type[] typeGenericArgumentTypes,
			int indent,
			StringBuilders builders)
		{
			FieldInfo field = enclosingType.GetField(jsonFieldName);
			Type fieldType = OverrideGenericType(
				field.FieldType,
				typeGenericArgumentTypes,
				typeTypeParams);
			TypeKind fieldTypeKind = GetTypeKind(fieldType);
			Type[] exceptionTypes = new Type[0];
			AppendGetter(
				field.Name,
				"Field",
				new ParameterInfo[0],
				enclosingTypeIsStatic,
				enclosingTypeKind,
				field.IsStatic,
				true,
				enclosingType,
				typeTypeParams,
				fieldType,
				fieldTypeKind,
				indent,
				exceptionTypes,
				builders);
			ParameterInfo setParam = new ParameterInfo();
			setParam.Name = "value";
			setParam.ParameterType = fieldType;
			setParam.IsOut = false;
			setParam.IsRef = false;
			setParam.DereferencedParameterType = setParam.ParameterType;
			setParam.Kind = GetTypeKind(
				setParam.DereferencedParameterType);
			ParameterInfo[] parameters = { setParam };
			AppendSetter(
				field.Name,
				"Field",
				parameters,
				enclosingTypeIsStatic,
				enclosingTypeKind,
				field.IsStatic,
				false,
				enclosingType,
				typeTypeParams,
				indent,
				exceptionTypes,
				builders);
		}
		
		static void AppendEvent(
			JsonEvent jsonEvent,
			Type enclosingType,
			bool enclosingTypeIsStatic,
			TypeKind enclosingTypeKind,
			Type[] typeTypeParams,
			int indent,
			StringBuilders builders)
		{
			EventInfo eventInfo = enclosingType.GetEvent(jsonEvent.Name);
			MethodInfo addMethod = eventInfo.GetAddMethod();
			MethodInfo removeMethod = eventInfo.GetRemoveMethod();
			Type eventType = eventInfo.EventHandlerType;
			string uppercaseEventName = char.ToUpper(jsonEvent.Name[0])
				+ jsonEvent.Name.Substring(1);
			
			ParameterInfo[] addRemoveParams = {
				new ParameterInfo {
					Name = "del",
					ParameterType = eventType,
					DereferencedParameterType = eventType,
					IsOut = false,
					IsRef = false,
					Kind = TypeKind.Class,
					IsVirtual = false
				}
			};
			
			AppendEventAddRemoveMethod(
				jsonEvent.Name,
				uppercaseEventName,
				"Add",
				addMethod.IsStatic,
				enclosingType,
				enclosingTypeKind,
				enclosingTypeIsStatic,
				typeTypeParams,
				addRemoveParams,
				indent,
				builders);
			AppendEventAddRemoveMethod(
				jsonEvent.Name,
				uppercaseEventName,
				"Remove",
				removeMethod.IsStatic,
				enclosingType,
				enclosingTypeKind,
				enclosingTypeIsStatic,
				typeTypeParams,
				addRemoveParams,
				indent,
				builders);
		}
		
		static void AppendEventAddRemoveMethod(
			string eventName,
			string uppercaseEventName,
			string operation,
			bool methodIsStatic,
			Type enclosingType,
			TypeKind enclosingTypeKind,
			bool enclosingTypeIsStatic,
			Type[] typeTypeParams,
			ParameterInfo[] methodParams,
			int indent,
			StringBuilders builders)
		{
			builders.TempStrBuilder.Length = 0;
			AppendNamespace(
				enclosingType.Namespace,
				string.Empty,
				builders.TempStrBuilder);
			AppendTypeNameWithoutGenericSuffix(
				enclosingType.Name,
				builders.TempStrBuilder);
			AppendTypeNames(
				typeTypeParams,
				builders.TempStrBuilder);
			builders.TempStrBuilder.Append(operation);
			builders.TempStrBuilder.Append("Event");
			builders.TempStrBuilder.Append(uppercaseEventName);
			string funcName = builders.TempStrBuilder.ToString();
			
			builders.TempStrBuilder[0] = char.ToLower(
				builders.TempStrBuilder[0]);
			string funcNameLower = builders.TempStrBuilder.ToString();
			
			builders.TempStrBuilder.Length = 0;
			builders.TempStrBuilder.Append(operation);
			builders.TempStrBuilder.Append(uppercaseEventName);
			string methodName = builders.TempStrBuilder.ToString();
			
			// C# init param
			AppendCsharpInitParam(
				funcNameLower,
				builders.CsharpInitParams);
			
			// C# delegate type
			AppendCsharpDelegateType(
				funcName,
				methodIsStatic,
				enclosingType,
				enclosingTypeKind,
				typeof(void),
				methodParams,
				builders.CsharpDelegateTypes);
			
			// C# init call arg
			AppendCsharpInitCallArg(
				funcName,
				builders.CsharpInitCall);
			
			// C# function
			AppendCsharpFunctionBeginning(
				enclosingType,
				funcName,
				methodIsStatic,
				enclosingTypeKind,
				typeof(void),
				methodParams,
				builders.CsharpFunctions);
			AppendCsharpFunctionCallSubject(
				enclosingType,
				methodIsStatic,
				builders.CsharpFunctions);
			builders.CsharpFunctions.Append('.');
			builders.CsharpFunctions.Append(eventName);
			builders.CsharpFunctions.Append(" += del;");
			AppendCsharpFunctionEnd(
				typeof(void),
				null,
				builders.CsharpFunctions);
			
			// C++ function pointer
			AppendCppFunctionPointerDefinition(
				funcName,
				methodIsStatic,
				enclosingType.Name,
				enclosingType.Namespace,
				enclosingTypeKind,
				methodParams,
				typeof(void),
				builders.CppFunctionPointers);
			
			// C++ method declaration
			Type cppReturnType = typeof(void);
			string cppMethodName = methodName;
			bool cppMethodIsStatic = methodIsStatic;
			ParameterInfo[] cppParameters = methodParams;
			ParameterInfo[] cppCallParameters = methodParams;
			AppendIndent(
				indent + 1,
				builders.CppTypeDefinitions);
			AppendCppMethodDeclaration(
				cppMethodName,
				enclosingTypeIsStatic,
				false,
				cppMethodIsStatic,
				cppReturnType,
				null,
				cppParameters,
				builders.CppTypeDefinitions);
			
			// C++ method definition
			AppendCppMethodDefinitionBegin(
				enclosingType.Name,
				cppReturnType,
				cppMethodName,
				typeTypeParams,
				null,
				cppParameters,
				indent,
				builders.CppMethodDefinitions);
			AppendIndent(
				indent,
				builders.CppMethodDefinitions);
			builders.CppMethodDefinitions.Append("{\n");
			AppendCppPluginFunctionCall(
				methodIsStatic,
				enclosingType.Name,
				enclosingType.Namespace,
				enclosingTypeKind,
				typeTypeParams,
				typeof(void),
				funcName,
				cppCallParameters,
				indent + 1,
				builders.CppMethodDefinitions);
			AppendIndent(
				indent,
				builders.CppMethodDefinitions);
			builders.CppMethodDefinitions.Append("}\n\t\n");
			
			// C++ init params
			AppendCppInitParam(
				funcNameLower,
				methodIsStatic,
				enclosingType.Name,
				enclosingType.Namespace,
				enclosingTypeKind,
				methodParams,
				typeof(void),
				builders.CppInitParams);
			
			// C++ init body
			AppendCppInitBody(
				funcName,
				funcNameLower,
				builders.CppInitBody);
		}
		
		static MethodInfo GetMethod(
			JsonMethod jsonMethod,
			Type enclosingType,
			Type[] typeTypeParams,
			Type[] genericArgTypes,
			MethodInfo[] methods)
		{
			// Map convenience method names to actual method names
			switch (jsonMethod.Name)
			{
				case "+x":
					jsonMethod.Name = "op_UnaryPlus";
					break;
				case "-x":
					jsonMethod.Name = "op_UnaryNegation";
					break;
				case "!x":
					jsonMethod.Name = "op_LogicalNot";
					break;
				case "~x":
					jsonMethod.Name = "op_OnesComplement";
					break;
				case "x++":
					jsonMethod.Name = "op_Increment";
					break;
				case "x--":
					jsonMethod.Name = "op_Decrement";
					break;
				case "(true)x":
					jsonMethod.Name = "op_True";
					break;
				case "(false)x":
					jsonMethod.Name = "op_False";
					break;
				case "implicit":
					jsonMethod.Name = "op_Implicit";
					break;
				case "explicit":
					jsonMethod.Name = "op_Explicit";
					break;
				case "x+y":
					jsonMethod.Name = "op_Addition";
					break;
				case "x-y":
					jsonMethod.Name = "op_Subtraction";
					break;
				case "x*y":
					jsonMethod.Name = "op_Multiply";
					break;
				case "x/y":
					jsonMethod.Name = "op_Division";
					break;
				case "x%y":
					jsonMethod.Name = "op_Modulus";
					break;
				case "x&y":
					jsonMethod.Name = "op_BitwiseAnd";
					break;
				case "x|y":
					jsonMethod.Name = "op_BitwiseOr";
					break;
				case "x^y":
					jsonMethod.Name = "op_ExclusiveOr";
					break;
				case "x<<y":
					jsonMethod.Name = "op_LeftShift";
					break;
				case "x>>y":
					jsonMethod.Name = "op_RightShift";
					break;
				case "x==y":
					jsonMethod.Name = "op_Equality";
					break;
				case "x!=y":
					jsonMethod.Name = "op_Inequality";
					break;
				case "x<y":
					jsonMethod.Name = "op_LessThan";
					break;
				case "x>y":
					jsonMethod.Name = "op_GreaterThan";
					break;
				case "x<=y":
					jsonMethod.Name = "op_LessThanOrEqual";
					break;
				case "x>=y":
					jsonMethod.Name = "op_GreaterThanOrEqual";
					break;
			}
			
			if (enclosingType.IsGenericType)
			{
				string[] overriddenParamTypeNames = OverrideGenericTypeNames(
					jsonMethod.ParamTypes,
					genericArgTypes,
					typeTypeParams);
				return GetMethod(
					enclosingType,
					methods,
					jsonMethod.Name,
					overriddenParamTypeNames);
			}
			else
			{
				return GetMethod(
					enclosingType,
					methods,
					jsonMethod.Name,
					jsonMethod.ParamTypes);
			}
		}
		
		static void AppendMethod(
			JsonMethod jsonMethod,
			Assembly[] assemblies,
			Type enclosingType,
			bool enclosingTypeIsStatic,
			TypeKind enclosingTypeKind,
			MethodInfo[] methods,
			Type[] typeTypeParams,
			Type[] genericArgTypes,
			int indent,
			StringBuilders builders)
		{
			MethodInfo method = GetMethod(
				jsonMethod,
				enclosingType,
				typeTypeParams,
				genericArgTypes,
				methods);
			
			Type[] exceptionTypes = GetTypes(
				jsonMethod.Exceptions,
				assemblies);
			
			if (jsonMethod.GenericParams != null)
			{
				// Generate for each set of generic types
				foreach (JsonGenericParams jsonGenericParams
					in jsonMethod.GenericParams)
				{
					Type[] methodTypeParams = GetTypes(
						jsonGenericParams.Types,
						assemblies);
					method = method.MakeGenericMethod(methodTypeParams);
					ParameterInfo[] parameters = ConvertParameters(
						method.GetParameters());
					Type returnType = method.ReturnType;
					TypeKind returnTypeKind = GetTypeKind(returnType);
					AppendMethod(
						enclosingType,
						method.Name,
						enclosingTypeIsStatic,
						enclosingTypeKind,
						method.IsStatic,
						jsonMethod.IsReadOnly,
						returnType,
						returnTypeKind,
						typeTypeParams,
						methodTypeParams,
						parameters,
						indent,
						exceptionTypes,
						builders);
				}
			}
			else
			{
				ParameterInfo[] parameters = ConvertParameters(
					method.GetParameters());
				Type returnType = method.ReturnType;
				TypeKind returnTypeKind = GetTypeKind(returnType);
				AppendMethod(
					enclosingType,
					method.Name,
					enclosingTypeIsStatic,
					enclosingTypeKind,
					method.IsStatic,
					jsonMethod.IsReadOnly,
					returnType,
					returnTypeKind,
					typeTypeParams,
					null,
					parameters,
					indent,
					exceptionTypes,
					builders);
			}
		}
		
		static Type OverrideGenericType(
			Type genericType,
			Type[] genericArgumentTypes,
			Type[] overrideTypes)
		{
			if (genericType.IsGenericParameter)
			{
				for (int i = 0, len = genericArgumentTypes.Length; i < len; ++i)
				{
					if (genericType == genericArgumentTypes[i])
					{
						return overrideTypes[i];
					}
				}
			}
			return genericType;
		}
		
		static void OverrideGenericParameterTypes(
			ParameterInfo[] parameters,
			Type[] typeGenericArgumentTypes,
			Type[] typeParams)
		{
			foreach (ParameterInfo info in parameters)
			{
				info.ParameterType = OverrideGenericType(
					info.ParameterType,
					typeGenericArgumentTypes,
					typeParams);
			}
		}
		
		static string[] OverrideGenericTypeNames(
			string[] typeNames,
			Type[] genericArgTypes,
			Type[] typeParams)
		{
			int numParams = typeNames.Length;
			string[] overriddenParamTypeNames = new string[numParams];
			for (int i = 0; i < numParams; ++i)
			{
				string typeName = typeNames[i];
				foreach (Type genericArgType in genericArgTypes)
				{
					if (CheckTypeNameMatches(
						typeName,
						genericArgType))
					{
						typeName = typeParams[i].FullName;
						break;
					}
				}
				overriddenParamTypeNames[i] = typeName;
			}
			return overriddenParamTypeNames;
		}
		
		static void AppendMethod(
			Type enclosingType,
			string methodName,
			bool enclosingTypeIsStatic,
			TypeKind enclosingTypeKind,
			bool methodIsStatic,
			bool isReadOnly,
			Type returnType,
			TypeKind returnTypeKind,
			Type[] enclosingTypeParams,
			Type[] methodTypeParams,
			ParameterInfo[] parameters,
			int indent,
			Type[] exceptionTypes,
			StringBuilders builders)
		{
			// Build uppercase function name
			builders.TempStrBuilder.Length = 0;
			AppendNamespace(
				enclosingType.Namespace,
				string.Empty,
				builders.TempStrBuilder);
			AppendTypeNameWithoutGenericSuffix(
				enclosingType.Name,
				builders.TempStrBuilder);
			AppendTypeNames(
				enclosingTypeParams,
				builders.TempStrBuilder);
			builders.TempStrBuilder.Append("Method");
			builders.TempStrBuilder.Append(methodName);
			AppendTypeNames(
				methodTypeParams,
				builders.TempStrBuilder);
			AppendParameterTypeNames(
				parameters,
				builders.TempStrBuilder);
			string funcName = builders.TempStrBuilder.ToString();
			
			// Build lowercase function name
			builders.TempStrBuilder[0] = char.ToLower(
				builders.TempStrBuilder[0]);
			string funcNameLower = builders.TempStrBuilder.ToString();
			
			// C# init param declaration
			AppendCsharpInitParam(
				funcNameLower,
				builders.CsharpInitParams);
			
			// C# delegate type
			AppendCsharpDelegateType(
				funcName,
				methodIsStatic,
				enclosingType,
				enclosingTypeKind,
				returnType,
				parameters,
				builders.CsharpDelegateTypes);
			
			// C# init call param
			AppendCsharpInitCallArg(
				funcName,
				builders.CsharpInitCall);
			
			// C# function
			AppendCsharpFunctionBeginning(
				enclosingType,
				funcName,
				methodIsStatic,
				enclosingTypeKind,
				returnType,
				parameters,
				builders.CsharpFunctions);
			if (methodName.StartsWith("op_"))
			{
				string op;
				switch (methodName)
				{
					case "op_UnaryPlus":
						op = "+";
						break;
					case "op_UnaryNegation":
						op = "-";
						break;
					case "op_LogicalNot":
						op = "!";
						break;
					case "op_OnesComplement":
						op = "~";
						break;
					case "op_Increment":
						op = "++";
						break;
					case "op_Decrement":
						op = "--";
						break;
					case "op_Implicit":
						op = string.Empty;
						break;
					case "op_Explicit":
						builders.TempStrBuilder.Length = 0;
						builders.TempStrBuilder.Append('(');
						AppendTypeNameWithoutGenericSuffix(
							returnType.Name,
							builders.TempStrBuilder);
						builders.TempStrBuilder.Append(')');
						op = builders.TempStrBuilder.ToString();
						break;
					case "op_True":
						op = "(true)";
						break;
					case "op_False":
						op = "(false)";
						break;
					case "op_Addition":
						op = "+";
						break;
					case "op_Subtraction":
						op = "-";
						break;
					case "op_Multiply":
						op = "*";
						break;
					case "op_Division":
						op = "/";
						break;
					case "op_Modulus":
						op = "%";
						break;
					case "op_BitwiseAnd":
						op = "&";
						break;
					case "op_BitwiseOr":
						op = "|";
						break;
					case "op_ExclusiveOr":
						op = "^";
						break;
					case "op_LeftShift":
						op = "<<";
						break;
					case "op_RightShift":
						op = ">>";
						break;
					case "op_Equality":
						op = "==";
						break;
					case "op_Inequality":
						op = "!=";
						break;
					case "op_LessThan":
						op = "<";
						break;
					case "op_GreaterThan":
						op = ">";
						break;
					case "op_LessThanOrEqual":
						op = "<=";
						break;
					case "op_GreaterThanOrEqual":
						op = ">=";
						break;
					default:
						throw new Exception(
							"Unsupported overloaded operator: " + methodName);
				}
				switch (parameters.Length)
				{
					case 1:
						builders.CsharpFunctions.Append(op);
						builders.CsharpFunctions.Append(parameters[0].Name);
						break;
					case 2:
						builders.CsharpFunctions.Append(parameters[0].Name);
						builders.CsharpFunctions.Append(' ');
						builders.CsharpFunctions.Append(op);
						builders.CsharpFunctions.Append(' ');
						builders.CsharpFunctions.Append(parameters[1].Name);
						break;
					default:
						throw new Exception(
							"Unsupported number of overloaded operator params: "
							+ parameters.Length);
				}
			}
			else
			{
				AppendCsharpFunctionCallSubject(
					enclosingType,
					methodIsStatic,
					builders.CsharpFunctions);
				builders.CsharpFunctions.Append('.');
				builders.CsharpFunctions.Append(methodName);
				AppendCSharpTypeParameters(
					methodTypeParams,
					builders.CsharpFunctions);
				AppendCsharpFunctionCallParameters(
					parameters,
					builders.CsharpFunctions);
			}
			builders.CsharpFunctions.Append(';');
			if (!isReadOnly
				&& enclosingTypeKind == TypeKind.ManagedStruct)
			{
				AppendStructStoreReplace(
					enclosingType,
					"thisHandle",
					"thiz",
					builders.CsharpFunctions);
			}
			AppendCsharpFunctionReturn(
				parameters,
				returnType,
				returnTypeKind,
				exceptionTypes,
				false,
				builders.CsharpFunctions);
			
			// C++ function pointer
			AppendCppFunctionPointerDefinition(
				funcName,
				methodIsStatic,
				enclosingType.Name,
				enclosingType.Namespace,
				enclosingTypeKind,
				parameters,
				returnType,
				builders.CppFunctionPointers);
			
			// C++ method declaration
			string cppMethodName;
			bool cppMethodIsStatic;
			ParameterInfo[] cppParameters;
			ParameterInfo[] cppCallParameters;
			Type cppReturnType = returnType;
			if (methodName.StartsWith("op_"))
			{
				switch (methodName)
				{
					case "op_UnaryPlus":
						cppMethodName = "operator+";
						break;
					case "op_UnaryNegation":
						cppMethodName = "operator-";
						break;
					case "op_LogicalNot":
						cppMethodName = "operator!";
						break;
					case "op_OnesComplement":
						cppMethodName = "operator~";
						break;
					case "op_Increment":
						cppMethodName = "operator++";
						break;
					case "op_Decrement":
						cppMethodName = "operator--";
						break;
					case "op_Implicit":
						builders.TempStrBuilder.Length = 0;
						builders.TempStrBuilder.Append("operator ");
						AppendCppTypeName(
							returnType,
							builders.TempStrBuilder);
						cppMethodName = builders.TempStrBuilder.ToString();
						cppReturnType = null;
						break;
					case "op_Explicit":
						builders.TempStrBuilder.Length = 0;
						builders.TempStrBuilder.Append("explicit operator ");
						AppendCppTypeName(
							returnType,
							builders.TempStrBuilder);
						cppMethodName = builders.TempStrBuilder.ToString();
						cppReturnType = null;
						break;
					case "op_True":
						cppMethodName = "TrueOperator";
						break;
					case "op_False":
						cppMethodName = "FalseOperator";
						break;
					case "op_Addition":
						cppMethodName = "operator+";
						break;
					case "op_Subtraction":
						cppMethodName = "operator-";
						break;
					case "op_Multiply":
						cppMethodName = "operator*";
						break;
					case "op_Division":
						cppMethodName = "operator/";
						break;
					case "op_Modulus":
						cppMethodName = "operator%";
						break;
					case "op_BitwiseAnd":
						cppMethodName = "operator&";
						break;
					case "op_BitwiseOr":
						cppMethodName = "operator|";
						break;
					case "op_ExclusiveOr":
						cppMethodName = "operator^";
						break;
					case "op_LeftShift":
						cppMethodName = "operator<<";
						break;
					case "op_RightShift":
						cppMethodName = "operator>>";
						break;
					case "op_Equality":
						cppMethodName = "operator==";
						break;
					case "op_Inequality":
						cppMethodName = "operator!=";
						break;
					case "op_LessThan":
						cppMethodName = "operator<";
						break;
					case "op_GreaterThan":
						cppMethodName = "operator>";
						break;
					case "op_LessThanOrEqual":
						cppMethodName = "operator<=";
						break;
					case "op_GreaterThanOrEqual":
						cppMethodName = "operator>=";
						break;
					default:
						throw new Exception(
							"Unsupported overloaded operator: " + methodName);
				}
				cppMethodIsStatic = false;
				ParameterInfo thisParam;
				switch (enclosingTypeKind)
				{
					case TypeKind.Class:
					case TypeKind.ManagedStruct:
						thisParam = new ParameterInfo{
							Name = "Handle",
							ParameterType = typeof(int),
							DereferencedParameterType = typeof(int),
							IsOut = false,
							IsRef = false,
							Kind = TypeKind.Primitive
						};
						break;
					default:
						thisParam = new ParameterInfo{
							Name = "*this",
							ParameterType = enclosingType,
							DereferencedParameterType = enclosingType,
							IsOut = false,
							IsRef = false,
							Kind = TypeKind.Primitive
						};
						break;
				}
				switch (parameters.Length)
				{
					case 1:
						cppParameters = new ParameterInfo[0];
						cppCallParameters = new [] {
							thisParam };
						break;
					case 2:
						cppParameters = new [] {
							parameters[0] };
						cppCallParameters = new [] {
							thisParam,
							parameters[0]
						};
						break;
					default:
						throw new Exception(
							"Unsupported number of overloaded operator parameters: "
							+ parameters.Length);
				}
			}
			else
			{
				cppMethodName = methodName;
				cppMethodIsStatic = methodIsStatic;
				cppParameters = parameters;
				cppCallParameters = parameters;
			}
			AppendIndent(
				indent + 1,
				builders.CppTypeDefinitions);
			AppendCppMethodDeclaration(
				cppMethodName,
				enclosingTypeIsStatic,
				false,
				cppMethodIsStatic,
				cppReturnType,
				methodTypeParams,
				cppParameters,
				builders.CppTypeDefinitions);
			
			// C++ method definition
			AppendCppMethodDefinitionBegin(
				enclosingType.Name,
				cppReturnType,
				cppMethodName,
				enclosingTypeParams,
				methodTypeParams,
				cppParameters,
				indent,
				builders.CppMethodDefinitions);
			AppendIndent(
				indent,
				builders.CppMethodDefinitions);
			builders.CppMethodDefinitions.Append("{\n");
			AppendCppPluginFunctionCall(
				methodIsStatic,
				enclosingType.Name,
				enclosingType.Namespace,
				enclosingTypeKind,
				enclosingTypeParams,
				returnType,
				funcName,
				cppCallParameters,
				indent + 1,
				builders.CppMethodDefinitions);
			AppendCppMethodReturn(
				returnType,
				returnTypeKind,
				indent + 1,
				builders.CppMethodDefinitions);
			AppendIndent(
				indent,
				builders.CppMethodDefinitions);
			builders.CppMethodDefinitions.Append("}\n\t\n");
			
			// C++ init params
			AppendCppInitParam(
				funcNameLower,
				methodIsStatic,
				enclosingType.Name,
				enclosingType.Namespace,
				enclosingTypeKind,
				parameters,
				returnType,
				builders.CppInitParams);
			
			// C++ init body
			AppendCppInitBody(
				funcName,
				funcNameLower,
				builders.CppInitBody);
		}
		
		static void AppendCSharpTypeParameters(
			Type[] typeParams,
			StringBuilder output
		)
		{
			if (typeParams != null && typeParams.Length > 0)
			{
				output.Append('<');
				for (int i = 0; i < typeParams.Length; ++i)
				{
					Type typeParam = typeParams[i];
					AppendCsharpTypeName(typeParam, output);
					if (i != typeParams.Length - 1)
					{
						output.Append(", ");
					}
				}
				output.Append('>');
			}
		}
		
		static void AppendCppTypeParameters(
			Type[] typeParams,
			StringBuilder output)
		{
			if (typeParams != null && typeParams.Length > 0)
			{
				output.Append('<');
				for (int i = 0; i < typeParams.Length; ++i)
				{
					Type typeParam = typeParams[i];
					AppendCppTypeName(typeParam, output);
					if (i != typeParams.Length - 1)
					{
						output.Append(", ");
					}
				}
				output.Append('>');
			}
		}
		
		static void AppendMonoBehaviour(
			JsonMonoBehaviour jsonMonoBehaviour,
			Assembly[] assemblies,
			StringBuilders builders)
		{
			Type type = GetType(
				jsonMonoBehaviour.Name,
				assemblies);
				
			// C++ Type Declaration
			int cppIndent = AppendCppTypeDeclaration(
				type.Namespace,
				type.Name,
				false,
				null,
				builders.CppTypeDeclarations);
			
			// C++ Type Definition (begin)
			AppendCppTypeDefinitionBegin(
				type.Name,
				type.Namespace,
				TypeKind.Class,
				null,
				"MonoBehaviour",
				"UnityEngine",
				null,
				false,
				cppIndent,
				builders.CppTypeDefinitions);
			
			// C++ method definition
			int cppMethodDefinitionsIndent = AppendCppMethodDefinitionsBegin(
				type.Name,
				type.Namespace,
				TypeKind.Class,
				null,
				"MonoBehaviour",
				"UnityEngine",
				null,
				false,
				(extraIndent, subject) => {},
				(extraIndent, subject) => {},
				cppIndent,
				builders.CppMethodDefinitions);
			AppendCppMethodDefinitionsEnd(
				cppMethodDefinitionsIndent,
				builders.CppMethodDefinitions);
			
			// C# Class extending MonoBehaviour
			int csharpIndent = AppendNamespaceBeginning(
				type.Namespace,
				builders.CsharpMonoBehaviours);
			AppendIndent(csharpIndent, builders.CsharpMonoBehaviours);
			builders.CsharpMonoBehaviours.Append("public class ");
			builders.CsharpMonoBehaviours.Append(type.Name);
			builders.CsharpMonoBehaviours.Append(" : UnityEngine.MonoBehaviour\n");
			AppendIndent(csharpIndent, builders.CsharpMonoBehaviours);
			builders.CsharpMonoBehaviours.Append("{\n");
			for (
				int messageIndex = 0;
				messageIndex < jsonMonoBehaviour.Messages.Length;
				++messageIndex)
			{
				// Find the MessageInfo
				string message = jsonMonoBehaviour.Messages[messageIndex];
				MessageInfo messageInfo = null;
				foreach (MessageInfo mi in messageInfos)
				{
					if (mi.Name == message)
					{
						messageInfo = mi;
						break;
					}
				}
				if (messageInfo == null)
				{
					builders.TempStrBuilder.Length = 0;
					builders.TempStrBuilder.Append("Unknown message '");
					builders.TempStrBuilder.Append(message);
					builders.TempStrBuilder.Append("'. Aborting.");
					throw new Exception(builders.TempStrBuilder.ToString());
				}
				
				// Build the C++ function name
				builders.TempStrBuilder.Length = 0;
				AppendNamespace(
					type.Namespace,
					string.Empty,
					builders.TempStrBuilder);
				builders.TempStrBuilder.Append(type.Name);
				builders.TempStrBuilder.Append(messageInfo.Name);
				string cppFunctionName = builders.TempStrBuilder.ToString();
				
				// Build ParameterInfos
				ParameterInfo[] parameters = ConvertParameters(
					messageInfo.ParameterTypes);
				int numParams = parameters.Length;
				
				// C++ Method Declaration
				AppendIndent(
					cppIndent + 1,
					builders.CppTypeDefinitions);
				AppendCppMethodDeclaration(
					messageInfo.Name,
					false,
					false,
					false,
					typeof(void),
					null,
					parameters,
					builders.CppTypeDefinitions);
				
				// C# message function
				AppendIndent(
					csharpIndent + 1,
					builders.CsharpMonoBehaviours);
				builders.CsharpMonoBehaviours.Append("public ");
				AppendCsharpTypeName(
					typeof(void),
					builders.CsharpMonoBehaviours);
				builders.CsharpMonoBehaviours.Append(' ');
				builders.CsharpMonoBehaviours.Append(messageInfo.Name);
				builders.CsharpMonoBehaviours.Append('(');
				for (int i = 0; i < numParams; ++i)
				{
					Type paramType = parameters[i].ParameterType;
					AppendCsharpTypeName(
						paramType,
						builders.CsharpMonoBehaviours);
					builders.CsharpMonoBehaviours.Append(' ');
					builders.CsharpMonoBehaviours.Append("param");
					builders.CsharpMonoBehaviours.Append(i);
					if (i != numParams - 1)
					{
						builders.CsharpMonoBehaviours.Append(", ");
					}
				}
				builders.CsharpMonoBehaviours.Append(")\n");
				AppendIndent(
					csharpIndent + 1,
					builders.CsharpMonoBehaviours);
				builders.CsharpMonoBehaviours.Append("{\n");
				AppendCppFunctionCall(
					cppFunctionName,
					parameters,
					typeof(void),
					false,
					csharpIndent + 2,
					builders.CsharpMonoBehaviours);
				AppendIndent(
					csharpIndent + 1,
					builders.CsharpMonoBehaviours);
				builders.CsharpMonoBehaviours.Append("}\n");
				if (messageIndex != jsonMonoBehaviour.Messages.Length - 1)
				{
					AppendIndent(
						csharpIndent + 1,
						builders.CsharpMonoBehaviours);
					builders.CsharpMonoBehaviours.Append('\n');
				}
				
				// C# Delegate
				AppendCsharpDelegate(
					false,
					type.Name,
					type.Namespace,
					null,
					messageInfo.Name,
					parameters,
					typeof(void),
					TypeKind.None,
					builders.CsharpDelegates);
				
				// C# Import
				AppendCsharpImport(
					type.Name,
					type.Namespace,
					null,
					messageInfo.Name,
					parameters,
					builders.CsharpImports);
				
				// C# GetDelegate Call
				AppendCsharpGetDelegateCall(
					type.Name,
					type.Namespace,
					null,
					messageInfo.Name,
					builders.CsharpGetDelegateCalls);
				
				// C++ Message
				builders.CppMonoBehaviourMessages.Append("DLLEXPORT void ");
				AppendCsharpDelegateName(
					type.Name,
					type.Namespace,
					null,
					messageInfo.Name,
					builders.CppMonoBehaviourMessages);
				builders.CppMonoBehaviourMessages.Append("(int32_t thisHandle");
				if (numParams > 0)
				{
					builders.CppMonoBehaviourMessages.Append(", ");
				}
				for (int i = 0; i < numParams; ++i)
				{
					ParameterInfo param = parameters[i];
					switch (param.Kind)
					{
						case TypeKind.FullStruct:
						case TypeKind.Primitive:
						case TypeKind.Enum:
							AppendCppTypeName(
								param.ParameterType,
								builders.CppMonoBehaviourMessages);
							builders.CppMonoBehaviourMessages.Append(" param");
							builders.CppMonoBehaviourMessages.Append(i);
							break;
						default:
							builders.CppMonoBehaviourMessages.Append("int32_t param");
							builders.CppMonoBehaviourMessages.Append(i);
							builders.CppMonoBehaviourMessages.Append("Handle");
							break;
					}
					if (i != numParams-1)
					{
						builders.CppMonoBehaviourMessages.Append(", ");
					}
				}
				builders.CppMonoBehaviourMessages.Append(")\n{\n\t");
				AppendCppTypeName(
					type,
					builders.CppMonoBehaviourMessages);
				builders.CppMonoBehaviourMessages.Append(" thiz(Plugin::InternalUse::Only, thisHandle);\n");
				for (int i = 0; i < numParams; ++i)
				{
					ParameterInfo param = parameters[i];
					if (param.Kind == TypeKind.Class
						|| param.Kind == TypeKind.ManagedStruct)
					{
						builders.CppMonoBehaviourMessages.Append('\t');
						AppendCppTypeName(
							param.ParameterType,
							builders.CppMonoBehaviourMessages);
						builders.CppMonoBehaviourMessages.Append(" param");
						builders.CppMonoBehaviourMessages.Append(i);
						builders.CppMonoBehaviourMessages.Append("(Plugin::InternalUse::Only, param");
						builders.CppMonoBehaviourMessages.Append(i);
						builders.CppMonoBehaviourMessages.Append("Handle);\n");
					}
				}
				builders.CppMonoBehaviourMessages.Append("\ttry\n");
				builders.CppMonoBehaviourMessages.Append("\t{\n");
				builders.CppMonoBehaviourMessages.Append("\t\tthiz.");
				builders.CppMonoBehaviourMessages.Append(messageInfo.Name);
				builders.CppMonoBehaviourMessages.Append("(");
				for (int i = 0; i < numParams; ++i)
				{
					builders.CppMonoBehaviourMessages.Append("param");
					builders.CppMonoBehaviourMessages.Append(i);
					if (i != numParams-1)
					{
						builders.CppMonoBehaviourMessages.Append(", ");
					}
				}
				builders.CppMonoBehaviourMessages.Append(");\n");
				builders.CppMonoBehaviourMessages.Append("\t}\n");
				builders.CppMonoBehaviourMessages.Append("\tcatch (System::Exception ex)\n");
				builders.CppMonoBehaviourMessages.Append("\t{\n");
				builders.CppMonoBehaviourMessages.Append("\t\tPlugin::SetException(ex.Handle);\n");
				builders.CppMonoBehaviourMessages.Append("\t}\n");
				builders.CppMonoBehaviourMessages.Append("\tcatch (...)\n");
				builders.CppMonoBehaviourMessages.Append("\t{\n");
				builders.CppMonoBehaviourMessages.Append("\t\tSystem::String msg = \"Unhandled exception in ");
				AppendCppTypeName(
					type,
					builders.CppMonoBehaviourMessages);
				builders.CppMonoBehaviourMessages.Append("::");
				builders.CppMonoBehaviourMessages.Append(messageInfo.Name);
				builders.CppMonoBehaviourMessages.Append("\";\n");
				builders.CppMonoBehaviourMessages.Append("\t\tSystem::Exception ex(msg);\n");
				builders.CppMonoBehaviourMessages.Append("\t\tPlugin::SetException(ex.Handle);\n");
				builders.CppMonoBehaviourMessages.Append("\t}\n");
				builders.CppMonoBehaviourMessages.Append("}\n\n\n");
			}
			
			// C# Class extending MonoBehaviour (end)
			AppendIndent(csharpIndent, builders.CsharpMonoBehaviours);
			builders.CsharpMonoBehaviours.Append("}\n");
			AppendNamespaceEnding(csharpIndent, builders.CsharpMonoBehaviours);
			
			// C++ Type Definition (end)
			AppendCppTypeDefinitionEnd(
				false,
				cppIndent,
				builders.CppTypeDefinitions);
		}
		
		static void AppendCppFunctionCall(
			string funcName,
			ParameterInfo[] parameters,
			Type returnType,
			bool enclosingTypeIsStatic,
			int indent,
			StringBuilder output)
		{
			foreach (ParameterInfo param in parameters)
			{
				if (param.Kind == TypeKind.Class
				    || param.Kind == TypeKind.ManagedStruct)
				{
					AppendIndent(
						indent,
						output);
					output.Append("int ");
					output.Append(param.Name);
					output.Append("Handle = ");
					AppendHandleStoreTypeName(
						param.DereferencedParameterType,
						output);
					output.Append('.');
					if (param.Kind == TypeKind.Class)
					{
						output.Append("GetHandle");
					}
					else
					{
						output.Append("Store");
					}
					output.Append('(');
					output.Append(param.Name);
					output.Append(");\n");
				}
			}
			if (!enclosingTypeIsStatic)
			{
				AppendIndent(
					indent,
					output);
				output.Append(
					"int thisHandle = NativeScript.Bindings.ObjectStore.GetHandle(this);\n");
			}
			AppendIndent(
				indent,
				output);
			if (returnType != typeof(void))
			{
				output.Append("var returnVal = ");
			}
			output.Append("NativeScript.Bindings.");
			output.Append(funcName);
			output.Append('(');
			if (!enclosingTypeIsStatic)
			{
				output.Append("thisHandle");
				if (parameters.Length > 0)
				{
					output.Append(", ");
				}
			}
			for (int i = 0; i < parameters.Length; ++i)
			{
				ParameterInfo param = parameters[i];
				output.Append(param.Name);
				if (param.Kind == TypeKind.Class
					|| param.Kind == TypeKind.ManagedStruct)
				{
					output.Append("Handle");
				}
				if (i != parameters.Length - 1)
				{
					output.Append(", ");
				}
			}
			output.Append(");\n");
			AppendIndent(
				indent,
				output);
			output.Append("if (NativeScript.Bindings.UnhandledCppException != null)\n");
			AppendIndent(
				indent,
				output);
			output.Append("{\n");
			AppendIndent(
				indent + 1,
				output);
			output.Append("Exception ex = NativeScript.Bindings.UnhandledCppException;\n");
			AppendIndent(
				indent + 1,
				output);
			output.Append("NativeScript.Bindings.UnhandledCppException = null;\n");
			AppendIndent(
				indent + 1,
				output);
			output.Append("throw ex;\n");
			AppendIndent(
				indent,
				output);
			output.Append("}\n");
		}
		
		static void AppendArray(
			JsonArray jsonArray,
			Assembly[] assemblies,
			StringBuilders builders)
		{
			// Get element type
			Type elementType = GetType(
				jsonArray.Type,
				assemblies);
			TypeKind elementTypeKind = GetTypeKind(elementType);
			
			// Default ranks to just 1
			int[] ranks;
			if (jsonArray.Ranks == null
				|| jsonArray.Ranks.Length == 0)
			{
				ranks = new[]{ 1 };
			}
			else
			{
				ranks = jsonArray.Ranks;
			}
			
			// C++ element proxy for [1-R] for all ranks R
			Type[] cppTypeParams = { elementType };
			foreach (int rank in ranks)
			{
				// Build array name
				builders.TempStrBuilder.Length = 0;
				AppendCppArrayTypeName(
					rank,
					builders.TempStrBuilder);
				string cppArrayTypeName = builders.TempStrBuilder.ToString();
				
				for (int i = 1; i <= rank; ++i)
				{
					AppendArrayElementProxy(
						elementType,
						elementTypeKind,
						i,
						rank,
						cppTypeParams,
						cppArrayTypeName,
						builders);
				}
			}
			
			foreach (int rank in ranks)
			{
				// Build array name
				builders.TempStrBuilder.Length = 0;
				AppendCppArrayTypeName(
					rank,
					builders.TempStrBuilder);
				string cppArrayTypeName = builders.TempStrBuilder.ToString();
				
				// Build array name with element type
				builders.TempStrBuilder.Append('<');
				AppendCppTypeName(
					elementType,
					builders.TempStrBuilder);
				builders.TempStrBuilder.Append('>');
				string cppGenericArrayTypeName = builders.TempStrBuilder.ToString();
				
				// Build element proxy name
				builders.TempStrBuilder.Length = 0;
				AppendCppArrayElementProxyName(
					1,
					rank,
					elementType,
					builders.TempStrBuilder);
				string cppElementProxyTypeName = builders.TempStrBuilder.ToString();
				
				// Build "TypeArray" name
				builders.TempStrBuilder.Length = 0;
				AppendBindingArrayTypeName(
					elementType.Name,
					elementType.Namespace,
					cppArrayTypeName,
					builders.TempStrBuilder);
				string bindingArrayTypeName = builders.TempStrBuilder.ToString();
				
				// MakeArrayType() creates a Type for a "vector"
				// MakeArrayType(int) creates a Type for a multi-dimensional array
				// Use MakeArrayType() instead of MakeArrayType(1) to create a vector
				// instead of a multi-dimensional array with one dimension.
				// This avoids problems like the name being "float[*]", which is
				// invalid C# code.
				Type arrayType;
				if (rank == 1)
				{
					arrayType = elementType.MakeArrayType();
				}
				else
				{
					arrayType = elementType.MakeArrayType(rank);
				}
				
				// C++ type declaration
				int indent = AppendCppTypeDeclaration(
					"System",
					cppArrayTypeName,
					false,
					cppTypeParams,
					builders.CppTypeDeclarations);
				
				// C++ type definition (beginning)
				AppendCppTypeDefinitionBegin(
					cppArrayTypeName,
					"System",
					TypeKind.Class,
					cppTypeParams,
					"Array",
					"System",
					null,
					false,
					indent,
					builders.CppTypeDefinitions);
				
				// C++ method definitions (beginning)
				int cppMethodDefinitionsIndent = AppendCppMethodDefinitionsBegin(
					cppArrayTypeName,
					"System",
					TypeKind.Class,
					cppTypeParams,
					"Array",
					"System",
					null,
					false,
					(extraIndent, subject) => {
						AppendIndent(
							extraIndent,
							builders.CppMethodDefinitions);
						builders.CppMethodDefinitions.Append(subject);
						builders.CppMethodDefinitions.Append(
							"InternalLength = 0;\n");
						if (rank > 1)
						{
							for (int i = 0; i < rank; ++i)
							{
								AppendIndent(
									extraIndent,
									builders.CppMethodDefinitions);
								builders.CppMethodDefinitions.Append(subject);
								builders.CppMethodDefinitions.Append(
									"InternalLengths[");
								builders.CppMethodDefinitions.Append(i);
								builders.CppMethodDefinitions.Append(
									"] = 0;\n");
							}
						}
					},
					(extraIndent, subject) => {
						AppendIndent(
							extraIndent,
							builders.CppMethodDefinitions);
						builders.CppMethodDefinitions.Append(
							"InternalLength = ");
						builders.CppMethodDefinitions.Append(subject);
						builders.CppMethodDefinitions.Append(
							"InternalLength;\n");
						if (rank > 1)
						{
							for (int i = 0; i < rank; ++i)
							{
								AppendIndent(
									extraIndent,
									builders.CppMethodDefinitions);
								builders.CppMethodDefinitions.Append(
									"InternalLengths[");
								builders.CppMethodDefinitions.Append(i);
								builders.CppMethodDefinitions.Append(
									"] = ");
								builders.CppMethodDefinitions.Append(subject);
								builders.CppMethodDefinitions.Append(
									"InternalLengths[");
								builders.CppMethodDefinitions.Append(i);
								builders.CppMethodDefinitions.Append(
									"];\n");
							}
						}
					},
					indent,
					builders.CppMethodDefinitions);
				
				// C++ fields
				AppendIndent(
					indent + 1,
					builders.CppTypeDefinitions);
				builders.CppTypeDefinitions.Append(
					"int32_t InternalLength;\n");
				if (rank > 1)
				{
					AppendIndent(
						indent + 1,
						builders.CppTypeDefinitions);
					builders.CppTypeDefinitions.Append(
						"int32_t InternalLengths[");
					builders.CppTypeDefinitions.Append(rank);
					builders.CppTypeDefinitions.Append("];\n");
				}
				
				AppendArrayConstructor(
					elementType,
					arrayType,
					cppArrayTypeName,
					rank,
					bindingArrayTypeName,
					indent,
					builders);
				
				// Base GetLength
				AppendArrayCppGetLengthFunction(
					indent,
					cppArrayTypeName,
					cppTypeParams,
					builders);
				
				// GetLength for multi-dimensional arrays
				if (rank > 1)
				{
					AppendArrayMultidimensionalGetLength(
						elementType,
						arrayType,
						cppArrayTypeName,
						rank,
						bindingArrayTypeName,
						indent,
						builders);
				}
				
				AppendArrayCppGetRankFunction(
					indent,
					cppArrayTypeName,
					cppTypeParams,
					rank,
					builders);
				
				AppendArrayGetItem(
					elementType,
					elementTypeKind,
					arrayType,
					cppArrayTypeName,
					rank,
					builders);
				
				AppendArraySetItem(
					elementType,
					arrayType,
					cppArrayTypeName,
					rank,
					builders);
				
				// C++ operator[] method declaration
				AppendIndent(
					indent + 1,
					builders.CppTypeDefinitions);
				builders.CppTypeDefinitions.Append("Plugin::");
				AppendCppArrayElementProxyName(
					1,
					rank,
					elementType,
					builders.CppTypeDefinitions);
				builders.CppTypeDefinitions.Append(' ');
				AppendTypeNameWithoutGenericSuffix(
					"operator[]",
					builders.CppTypeDefinitions);
				builders.CppTypeDefinitions.Append("(int32_t index);\n");
				
				// C++ operator[] method definition
				AppendCppArrayIndexOperatorMethodDefinition(
					0,
					cppMethodDefinitionsIndent,
					cppGenericArrayTypeName,
					"System",
					cppElementProxyTypeName,
					builders.CppMethodDefinitions);
				
				// C++ type definition (end)
				AppendCppTypeDefinitionEnd(
					false,
					indent,
					builders.CppTypeDefinitions);
				
				// C++ method definitions (ending)
				AppendCppMethodDefinitionsEnd(
					cppMethodDefinitionsIndent,
					builders.CppMethodDefinitions);
			}
		}
		
		static void AppendCppArrayIndexOperatorMethodDefinition(
			int rank,
			int indent,
			string enclosingTypeName,
			string enclosingTypeNamespace,
			string nextCppElementProxyTypeName,
			StringBuilder output)
		{
			AppendIndent(
				indent,
				output);
			AppendCppTypeName(
				"Plugin",
				nextCppElementProxyTypeName,
				output);
			output.Append(' ');
			output.Append(enclosingTypeNamespace);
			output.Append("::");
			AppendTypeNameWithoutGenericSuffix(
				enclosingTypeName,
				output);
			output.Append("::operator[](int32_t index)\n");
			AppendIndent(
				indent,
				output);
			output.Append("{\n");
			AppendIndent(
				indent + 1,
				output);
			output.Append("return Plugin::");
			output.Append(nextCppElementProxyTypeName);
			output.Append("(Plugin::InternalUse::Only, Handle, ");
			for (int i = 0; i < rank; ++i)
			{
				output.Append("Index");
				output.Append(i);
				output.Append(", ");
			}
			output.Append("index);\n");
			AppendIndent(
				indent,
				output);
			output.Append("}\n");
			AppendIndent(
				indent,
				output);
			output.Append('\n');
		}
		
		static void AppendCppArrayTypeName(
			int rank,
			StringBuilder output)
		{
			output.Append("Array");
			output.Append(rank);
		}
		
		static void AppendCppArrayElementProxyName(
			int rank,
			int maxRank,
			Type elementType,
			StringBuilder output)
		{
			output.Append("ArrayElementProxy");
			output.Append(rank);
			output.Append('_');
			output.Append(maxRank);
			output.Append('<');
			AppendCppTypeName(
				elementType,
				output);
			output.Append('>');
		}
		
		static void AppendBindingArrayTypeName(
			string elementTypeName,
			string elementTypeNamespace,
			string cppArrayTypeName,
			StringBuilder output)
		{
			AppendNamespace(
				elementTypeNamespace,
				string.Empty,
				output);
			AppendTypeNameWithoutGenericSuffix(
				elementTypeName,
				output);
			output.Append(cppArrayTypeName);
		}
		
		static ParameterInfo[] BuildArrayGetItemsParams(
			int rank,
			string indexName)
		{
			ParameterInfo[] parameters = new ParameterInfo[rank];
			for (int i = 0; i < rank; ++i)
			{
				ParameterInfo param = new ParameterInfo();
				param.Name = indexName + i;
				param.ParameterType = typeof(int);
				param.IsOut = false;
				param.IsRef = false;
				param.DereferencedParameterType = param.ParameterType;
				param.Kind = GetTypeKind(
					param.DereferencedParameterType);
				parameters[i] = param;
			}
			return parameters;
		}
		
		static ParameterInfo[] BuildArraySetItemsParams(
			int rank,
			string indexName,
			Type elementType)
		{
			ParameterInfo[] parameters = new ParameterInfo[rank+1];
			for (int i = 0; i < rank; ++i)
			{
				ParameterInfo param = new ParameterInfo();
				param.Name = indexName + i;
				param.ParameterType = typeof(int);
				param.IsOut = false;
				param.IsRef = false;
				param.DereferencedParameterType = param.ParameterType;
				param.Kind = GetTypeKind(
					param.DereferencedParameterType);
				parameters[i] = param;
			}
			
			ParameterInfo lastParamInfo = new ParameterInfo();
			lastParamInfo.Name = "item";
			lastParamInfo.ParameterType = elementType;
			lastParamInfo.IsOut = false;
			lastParamInfo.IsRef = false;
			lastParamInfo.DereferencedParameterType = lastParamInfo.ParameterType;
			lastParamInfo.Kind = GetTypeKind(
				lastParamInfo.DereferencedParameterType);
			parameters[rank] = lastParamInfo;
			
			return parameters;
		}
		
		static void AppendArrayGetItemFuncName(
			string elementTypeName,
			string elementTypeNamespace,
			string bindingArrayTypeName,
			int rank,
			StringBuilder output)
		{
			AppendNamespace(
				elementTypeNamespace,
				string.Empty,
				output);
			output.Append(elementTypeName);
			AppendTypeNameWithoutGenericSuffix(
				bindingArrayTypeName,
				output);
			output.Append("GetItem");
			output.Append(rank);
		}
		
		static void AppendArraySetItemFuncName(
			string elementTypeName,
			string elementTypeNamespace,
			string bindingArrayTypeName,
			int rank,
			StringBuilder output)
		{
			AppendNamespace(
				elementTypeNamespace,
				string.Empty,
				output);
			output.Append(elementTypeName);
			AppendTypeNameWithoutGenericSuffix(
				bindingArrayTypeName,
				output);
			output.Append("SetItem");
			output.Append(rank);
		}
		
		static void AppendArrayElementProxy(
			Type elementType,
			TypeKind elementTypeKind,
			int rank,
			int maxRank,
			Type[] cppTypeParams,
			string cppArrayTypeName,
			StringBuilders builders)
		{
			// Build element proxy name
			builders.TempStrBuilder.Length = 0;
			AppendCppArrayElementProxyName(
				rank,
				maxRank,
				elementType,
				builders.TempStrBuilder);
			string cppElementProxyTypeName = builders.TempStrBuilder.ToString();
			
			// Build next element proxy name
			builders.TempStrBuilder.Length = 0;
			AppendCppArrayElementProxyName(
				rank + 1,
				maxRank,
				elementType,
				builders.TempStrBuilder);
			string nextCppElementProxyTypeName = builders.TempStrBuilder.ToString();
			
			// GetItem name
			builders.TempStrBuilder.Length = 0;
			AppendArrayGetItemFuncName(
				elementType.Name,
				elementType.Namespace,
				cppArrayTypeName,
				rank,
				builders.TempStrBuilder);
			string getItemFuncName = builders.TempStrBuilder.ToString();
			
			// SetItem name
			builders.TempStrBuilder.Length = 0;
			AppendArraySetItemFuncName(
				elementType.Name,
				elementType.Namespace,
				cppArrayTypeName,
				rank,
				builders.TempStrBuilder);
			string setItemFuncName = builders.TempStrBuilder.ToString();
			
			// GetItem call params
			ParameterInfo[] getItemCallParams = BuildArrayGetItemsParams(
				rank,
				"Index");
			
			// SetItem params
			ParameterInfo[] setItemCallParams = BuildArraySetItemsParams(
				rank,
				"Index",
				elementType);
			
			// C++ element proxy type declaration
			int indent = AppendNamespaceBeginning(
				"Plugin",
				builders.CppTypeDeclarations);
			AppendIndent(indent, builders.CppTypeDeclarations);
			builders.CppTypeDeclarations.Append("template<> struct ");
			AppendTypeNameWithoutGenericSuffix(
				cppElementProxyTypeName,
				builders.CppTypeDeclarations);
			builders.CppTypeDeclarations.Append(";\n");
			AppendNamespaceEnding(
				indent,
				builders.CppTypeDeclarations);
			builders.CppTypeDeclarations.Append('\n');
			
			// C++ element proxy type definition
			AppendNamespaceBeginning(
				"Plugin",
				builders.CppTypeDefinitions);
			AppendIndent(
				indent,
				builders.CppTypeDefinitions);
			builders.CppTypeDefinitions.Append("template<> struct ");
			builders.CppTypeDefinitions.Append(cppElementProxyTypeName);
			builders.CppTypeDefinitions.Append('\n');
			AppendIndent(
				indent,
				builders.CppTypeDefinitions);
			builders.CppTypeDefinitions.Append("{\n");
			AppendIndent(
				indent + 1,
				builders.CppTypeDefinitions);
			builders.CppTypeDefinitions.Append("int32_t Handle;\n");
			for (int i = 0; i < rank; ++i)
			{
				AppendIndent(
					indent + 1,
					builders.CppTypeDefinitions);
				builders.CppTypeDefinitions.Append("int32_t Index");
				builders.CppTypeDefinitions.Append(i);
				builders.CppTypeDefinitions.Append(";\n");
			}
			AppendIndent(
				indent + 1,
				builders.CppTypeDefinitions);
			builders.CppTypeDefinitions.Append(cppElementProxyTypeName);
			builders.CppTypeDefinitions.Append(
				"(Plugin::InternalUse iu, int32_t handle, ");
			for (int i = 0; i < rank; ++i)
			{
				builders.CppTypeDefinitions.Append("int32_t index");
				builders.CppTypeDefinitions.Append(i);
				if (i != rank - 1)
				{
					builders.CppTypeDefinitions.Append(", ");
				}
			}
			builders.CppTypeDefinitions.Append(");\n");
			if (rank == maxRank)
			{
				AppendIndent(
					indent + 1,
					builders.CppTypeDefinitions);
				builders.CppTypeDefinitions.Append("void operator=(");
				AppendCppTypeName(
					elementType,
					builders.CppTypeDefinitions);
				builders.CppTypeDefinitions.Append(" item);\n");
				AppendIndent(
					indent + 1,
					builders.CppTypeDefinitions);
				builders.CppTypeDefinitions.Append("operator ");
				AppendCppTypeName(
					elementType,
					builders.CppTypeDefinitions);
				builders.CppTypeDefinitions.Append("();\n");
			}
			else
			{
				AppendIndent(
					indent + 1,
					builders.CppTypeDefinitions);
				builders.CppTypeDefinitions.Append("Plugin::");
				AppendCppArrayElementProxyName(
					rank + 1,
					maxRank,
					elementType,
					builders.CppTypeDefinitions);
				builders.CppTypeDefinitions.Append(" operator[](");
				builders.CppTypeDefinitions.Append("int32_t index);\n");
			}
			AppendIndent(
				indent,
				builders.CppTypeDefinitions);
			builders.CppTypeDefinitions.Append("};\n");
			builders.CppTypeDefinitions.Append("}\n");
			builders.CppTypeDefinitions.Append('\n');
			
			// C++ element proxy method definitions (beginning)
			int cppMethodDefinitionsIndent = AppendNamespaceBeginning(
				"Plugin",
				builders.CppMethodDefinitions);
			
			// C++ element proxy constructor definition
			AppendIndent(
				cppMethodDefinitionsIndent,
				builders.CppMethodDefinitions);
			builders.CppMethodDefinitions.Append(cppElementProxyTypeName);
			builders.CppMethodDefinitions.Append(
				"::ArrayElementProxy");
			builders.CppMethodDefinitions.Append(rank);
			builders.CppMethodDefinitions.Append('_');
			builders.CppMethodDefinitions.Append(maxRank);
			builders.CppMethodDefinitions.Append(
				"(Plugin::InternalUse iu, int32_t handle, ");
			for (int i = 0; i < rank; ++i)
			{
				builders.CppMethodDefinitions.Append("int32_t index");
				builders.CppMethodDefinitions.Append(i);
				if (i != rank - 1)
				{
					builders.CppMethodDefinitions.Append(", ");
				}
			}
			builders.CppMethodDefinitions.Append(")\n");
			AppendIndent(
				cppMethodDefinitionsIndent,
				builders.CppMethodDefinitions);
			builders.CppMethodDefinitions.Append("{\n");
			AppendIndent(
				cppMethodDefinitionsIndent + 1,
				builders.CppMethodDefinitions);
			builders.CppMethodDefinitions.Append("Handle = handle;\n");
			for (int i = 0; i < rank; ++i)
			{
				AppendIndent(
					cppMethodDefinitionsIndent + 1,
					builders.CppMethodDefinitions);
				builders.CppMethodDefinitions.Append("Index");
				builders.CppMethodDefinitions.Append(i);
				builders.CppMethodDefinitions.Append(" = index");
				builders.CppMethodDefinitions.Append(i);
				builders.CppMethodDefinitions.Append(";\n");
			}
			AppendIndent(
				cppMethodDefinitionsIndent,
				builders.CppMethodDefinitions);
			builders.CppMethodDefinitions.Append("}\n");
			AppendIndent(
				cppMethodDefinitionsIndent,
				builders.CppMethodDefinitions);
			builders.CppMethodDefinitions.Append('\n');
			
			if (rank == maxRank)
			{
				// C++ element proxy operator= definition
				AppendIndent(
					cppMethodDefinitionsIndent,
					builders.CppMethodDefinitions);
				builders.CppMethodDefinitions.Append("void ");
				builders.CppMethodDefinitions.Append(cppElementProxyTypeName);
				builders.CppMethodDefinitions.Append("::");
				builders.CppMethodDefinitions.Append("operator=(");
				AppendCppTypeName(
					elementType,
					builders.CppMethodDefinitions);
				builders.CppMethodDefinitions.Append(" item)\n");
				AppendIndent(
					cppMethodDefinitionsIndent,
					builders.CppMethodDefinitions);
				builders.CppMethodDefinitions.Append("{\n");
				AppendCppPluginFunctionCall(
					false,
					cppArrayTypeName,
					"System",
					TypeKind.Class,
					cppTypeParams,
					typeof(void),
					setItemFuncName,
					setItemCallParams,
					cppMethodDefinitionsIndent + 1,
					builders.CppMethodDefinitions);
				AppendIndent(
					cppMethodDefinitionsIndent,
					builders.CppMethodDefinitions);
				builders.CppMethodDefinitions.Append("}\n");
				AppendIndent(
					cppMethodDefinitionsIndent,
					builders.CppMethodDefinitions);
				builders.CppMethodDefinitions.Append('\n');
				
				// C++ element proxy type conversion operator definition
				AppendIndent(
					cppMethodDefinitionsIndent,
					builders.CppMethodDefinitions);
				builders.CppMethodDefinitions.Append(cppElementProxyTypeName);
				builders.CppMethodDefinitions.Append("::");
				builders.CppMethodDefinitions.Append("operator ");
				AppendCppTypeName(
					elementType,
					builders.CppMethodDefinitions);
				builders.CppMethodDefinitions.Append("()\n");
				AppendIndent(
					cppMethodDefinitionsIndent,
					builders.CppMethodDefinitions);
				builders.CppMethodDefinitions.Append("{\n");
				AppendCppPluginFunctionCall(
					false,
					cppArrayTypeName,
					"System",
					TypeKind.Class,
					cppTypeParams,
					elementType,
					getItemFuncName,
					getItemCallParams,
					indent + 1,
					builders.CppMethodDefinitions);
				AppendCppMethodReturn(
					elementType,
					elementTypeKind,
					indent + 1,
					builders.CppMethodDefinitions);
				AppendIndent(
					cppMethodDefinitionsIndent,
					builders.CppMethodDefinitions);
				builders.CppMethodDefinitions.Append("}\n");
			}
			else
			{
				AppendCppArrayIndexOperatorMethodDefinition(
					rank,
					cppMethodDefinitionsIndent,
					cppElementProxyTypeName,
					"Plugin",
					nextCppElementProxyTypeName,
					builders.CppMethodDefinitions);
			}
			
			// C++ method definitions (ending)
			AppendCppMethodDefinitionsEnd(
				cppMethodDefinitionsIndent,
				builders.CppMethodDefinitions);
		}
		
		static void AppendArrayConstructor(
			Type elementType,
			Type arrayType,
			string cppArrayTypeName,
			int rank,
			string csharpTypeName,
			int indent,
			StringBuilders builders)
		{
			builders.TempStrBuilder.Length = 0;
			AppendNamespace(
				elementType.Namespace,
				string.Empty,
				builders.TempStrBuilder);
			AppendTypeNameWithoutGenericSuffix(
				csharpTypeName,
				builders.TempStrBuilder);
			builders.TempStrBuilder.Append("Constructor");
			builders.TempStrBuilder.Append(rank);
			string funcName = builders.TempStrBuilder.ToString();
			
			builders.TempStrBuilder[0] = char.ToLower(
				builders.TempStrBuilder[0]);
			string funcNameLower = builders.TempStrBuilder.ToString();
			
			ParameterInfo[] parameters = new ParameterInfo[rank];
			for (int i = 0; i < rank; ++i)
			{
				ParameterInfo info = new ParameterInfo();
				info.Name = "length" + i;
				info.ParameterType = typeof(int);
				info.IsOut = false;
				info.IsRef = false;
				info.DereferencedParameterType = info.ParameterType;
				info.Kind = TypeKind.Primitive;
				parameters[i] = info;
			}
			
			// C# Delegate Type
			AppendCsharpDelegateType(
				funcName,
				true,
				arrayType,
				TypeKind.Class,
				arrayType,
				parameters,
				builders.CsharpDelegateTypes);
			
			// C# Init Call
			AppendCsharpInitCallArg(
				funcName,
				builders.CsharpInitCall);
			
			// C# Init Param
			AppendCsharpInitParam(
				funcNameLower,
				builders.CsharpInitParams);
			
			// C# function
			AppendCsharpFunctionBeginning(
				arrayType,
				funcName,
				true,
				TypeKind.Class,
				arrayType,
				parameters,
				builders.CsharpFunctions);
			AppendHandleStoreTypeName(
				arrayType,
				builders.CsharpFunctions);
			builders.CsharpFunctions.Append(".Store(new ");
			AppendCsharpTypeName(
				elementType,
				builders.CsharpFunctions);
			builders.CsharpFunctions.Append('[');
			for (int i = 0; i < rank; ++i)
			{
				builders.CsharpFunctions.Append("length");
				builders.CsharpFunctions.Append(i);
				if (i != rank-1)
				{
					builders.CsharpFunctions.Append(", ");
				}
			}
			builders.CsharpFunctions.Append("]);");
			AppendCsharpFunctionReturn(
				parameters,
				arrayType,
				TypeKind.Class,
				null,
				true,
				builders.CsharpFunctions);
			
			// C++ function pointer definition
			AppendCppFunctionPointerDefinition(
				funcName,
				true,
				cppArrayTypeName,
				"System",
				TypeKind.Class,
				parameters,
				arrayType,
				builders.CppFunctionPointers);
			
			// C++ init param
			AppendCppInitParam(
				funcNameLower,
				true,
				cppArrayTypeName,
				"System",
				TypeKind.Class,
				parameters,
				arrayType,
				builders.CppInitParams);
			
			// C++ init body
			AppendCppInitBody(
				funcName,
				funcNameLower,
				builders.CppInitBody);
			
			// C++ method declaration
			AppendIndent(
				indent + 1,
				builders.CppTypeDefinitions);
			AppendCppMethodDeclaration(
				cppArrayTypeName,
				false,
				false,
				false,
				null,
				null,
				parameters,
				builders.CppTypeDefinitions);
			
			// C++ method definition
			Type[] cppTypeParams = { elementType };
			AppendCppMethodDefinitionBegin(
				cppArrayTypeName,
				null,
				cppArrayTypeName,
				cppTypeParams,
				null,
				parameters,
				indent,
				builders.CppMethodDefinitions);
			AppendIndent(
				indent + 1,
				builders.CppMethodDefinitions);
			builders.CppMethodDefinitions.Append(" : ");
			AppendCppTypeName(
				"System",
				"Array",
				builders.CppMethodDefinitions);
			builders.CppMethodDefinitions.Append("(nullptr)\n");
			AppendIndent(
				indent,
				builders.CppMethodDefinitions);
			builders.CppMethodDefinitions.Append("{\n");
			AppendCppPluginFunctionCall(
				true,
				cppArrayTypeName,
				"System",
				TypeKind.Class,
				cppTypeParams,
				arrayType,
				funcName,
				parameters,
				indent + 1,
				builders.CppMethodDefinitions);
			AppendIndent(
				indent + 1,
				builders.CppMethodDefinitions);
			builders.CppMethodDefinitions.Append(
				"Handle = returnValue;\n");
			AppendIndent(
				indent + 1,
				builders.CppMethodDefinitions);
			builders.CppMethodDefinitions.Append(
				"if (returnValue)\n");
			AppendIndent(
				indent + 1,
				builders.CppMethodDefinitions);
			builders.CppMethodDefinitions.Append(
				"{\n");
			AppendIndent(
				indent + 2,
				builders.CppMethodDefinitions);
			AppendReferenceManagedHandleFunctionCall(
				cppArrayTypeName,
				"System",
				TypeKind.Class,
				cppTypeParams,
				"returnValue",
				builders.CppMethodDefinitions);
			builders.CppMethodDefinitions.Append(";\n");
			if (rank > 1)
			{
				AppendIndent(
					indent + 2,
					builders.CppMethodDefinitions);
				builders.CppMethodDefinitions.Append("InternalLength = ");
				for (int i = 0; i < rank; ++i)
				{
					builders.CppMethodDefinitions.Append("length");
					builders.CppMethodDefinitions.Append(i);
					if (i != rank - 1)
					{
						builders.CppMethodDefinitions.Append(" * ");
					}
				}
				builders.CppMethodDefinitions.Append(";\n");
				for (int i = 0; i < rank; ++i)
				{
					AppendIndent(
						indent + 2,
						builders.CppMethodDefinitions);
					builders.CppMethodDefinitions.Append("InternalLengths[");
					builders.CppMethodDefinitions.Append(i);
					builders.CppMethodDefinitions.Append("] = length");
					builders.CppMethodDefinitions.Append(i);
					builders.CppMethodDefinitions.Append(";\n");
				}
			}
			else
			{
				AppendIndent(
					indent + 2,
					builders.CppMethodDefinitions);
				builders.CppMethodDefinitions.Append(
					"InternalLength = length0;\n");
			}
			AppendIndent(
				indent + 1,
				builders.CppMethodDefinitions);
			builders.CppMethodDefinitions.Append(
				"}\n");
			AppendIndent(
				indent,
				builders.CppMethodDefinitions);
			builders.CppMethodDefinitions.Append("}\n");
			AppendIndent(
				indent,
				builders.CppMethodDefinitions);
			builders.CppMethodDefinitions.Append("\n");
		}
		
		static void AppendArrayCppGetLengthFunction(
			int indent,
			string cppArrayTypeName,
			Type[] cppTypeParams,
			StringBuilders builders)
		{
			ParameterInfo[] parameters = new ParameterInfo[0];
			
			// C++ method declaration
			AppendIndent(
				indent + 1,
				builders.CppTypeDefinitions);
			AppendCppMethodDeclaration(
				"GetLength",
				false,
				false,
				false,
				typeof(int),
				null,
				parameters,
				builders.CppTypeDefinitions);
			
			// C++ method definition
			AppendCppMethodDefinitionBegin(
				cppArrayTypeName,
				typeof(int),
				"GetLength",
				cppTypeParams,
				null,
				parameters,
				indent,
				builders.CppMethodDefinitions);
			AppendIndent(
				indent,
				builders.CppMethodDefinitions);
			builders.CppMethodDefinitions.Append("{\n");
			AppendIndent(
				indent + 1,
				builders.CppMethodDefinitions);
			builders.CppMethodDefinitions.Append(
				"int32_t returnVal = InternalLength;\n");
			AppendIndent(
				indent + 1,
				builders.CppMethodDefinitions);
			builders.CppMethodDefinitions.Append("if (returnVal == 0)\n");
			AppendIndent(
				indent + 1,
				builders.CppMethodDefinitions);
			builders.CppMethodDefinitions.Append("{\n");
			AppendIndent(
				indent + 2,
				builders.CppMethodDefinitions);
			builders.CppMethodDefinitions.Append(
				"returnVal = Array::GetLength();\n");
			AppendIndent(
				indent + 2,
				builders.CppMethodDefinitions);
			builders.CppMethodDefinitions.Append(
				"InternalLength = returnVal;\n");
			AppendIndent(
				indent + 1,
				builders.CppMethodDefinitions);
			builders.CppMethodDefinitions.Append("};\n");
			AppendIndent(
				indent + 1,
				builders.CppMethodDefinitions);
			builders.CppMethodDefinitions.Append("return returnVal;\n");
			AppendIndent(
				indent,
				builders.CppMethodDefinitions);
			builders.CppMethodDefinitions.Append("}\n");
			AppendIndent(
				indent,
				builders.CppMethodDefinitions);
			builders.CppMethodDefinitions.Append('\n');
		}
		
		static void AppendArrayCppGetRankFunction(
			int indent,
			string cppArrayTypeName,
			Type[] cppTypeParams,
			int rank,
			StringBuilders builders)
		{
			ParameterInfo[] parameters = new ParameterInfo[0];
			
			// C++ method declaration
			AppendIndent(
				indent + 1,
				builders.CppTypeDefinitions);
			AppendCppMethodDeclaration(
				"GetRank",
				false,
				false,
				false,
				typeof(int),
				null,
				parameters,
				builders.CppTypeDefinitions);
			
			// C++ method definition
			AppendCppMethodDefinitionBegin(
				cppArrayTypeName,
				typeof(int),
				"GetRank",
				cppTypeParams,
				null,
				parameters,
				indent,
				builders.CppMethodDefinitions);
			AppendIndent(
				indent,
				builders.CppMethodDefinitions);
			builders.CppMethodDefinitions.Append("{\n");
			AppendIndent(
				indent + 1,
				builders.CppMethodDefinitions);
			builders.CppMethodDefinitions.Append("return ");
			builders.CppMethodDefinitions.Append(rank);
			builders.CppMethodDefinitions.Append(";\n");
			AppendIndent(
				indent,
				builders.CppMethodDefinitions);
			builders.CppMethodDefinitions.Append("}\n");
			AppendIndent(
				indent,
				builders.CppMethodDefinitions);
			builders.CppMethodDefinitions.Append('\n');
		}
		
		static void AppendArrayMultidimensionalGetLength(
			Type elementType,
			Type arrayType,
			string cppArrayTypeName,
			int rank,
			string csharpTypeName,
			int indent,
			StringBuilders builders)
		{
			builders.TempStrBuilder.Length = 0;
			AppendNamespace(
				elementType.Namespace,
				string.Empty,
				builders.TempStrBuilder);
			AppendTypeNameWithoutGenericSuffix(
				csharpTypeName,
				builders.TempStrBuilder);
			builders.TempStrBuilder.Append("GetLength");
			builders.TempStrBuilder.Append(rank);
			string funcName = builders.TempStrBuilder.ToString();
			
			builders.TempStrBuilder[0] = char.ToLower(
				builders.TempStrBuilder[0]);
			string funcNameLower = builders.TempStrBuilder.ToString();
			
			ParameterInfo[] parameters = {
				new ParameterInfo {
					Name = "dimension",
					ParameterType = typeof(int),
					IsOut = false,
					IsRef = false,
					DereferencedParameterType = typeof(int),
					Kind = TypeKind.Primitive,
				}
			};
			
			// C# Delegate Type
			AppendCsharpDelegateType(
				funcName,
				false,
				arrayType,
				TypeKind.Class,
				typeof(int),
				parameters,
				builders.CsharpDelegateTypes);
			
			// C# Init Call
			AppendCsharpInitCallArg(
				funcName,
				builders.CsharpInitCall);
			
			// C# Init Param
			AppendCsharpInitParam(
				funcNameLower,
				builders.CsharpInitParams);
			
			// C# function
			AppendCsharpFunctionBeginning(
				arrayType,
				funcName,
				false,
				TypeKind.Class,
				typeof(int),
				parameters,
				builders.CsharpFunctions);
			builders.CsharpFunctions.Append(
				"thiz.GetLength(dimension);");
			AppendCsharpFunctionReturn(
				parameters,
				typeof(int),
				TypeKind.Primitive,
				null,
				false,
				builders.CsharpFunctions);
			
			// C++ function pointer definition
			AppendCppFunctionPointerDefinition(
				funcName,
				false,
				cppArrayTypeName,
				"System",
				TypeKind.Class,
				parameters,
				arrayType,
				builders.CppFunctionPointers);
			
			// C++ init param
			AppendCppInitParam(
				funcNameLower,
				false,
				cppArrayTypeName,
				"System",
				TypeKind.Class,
				parameters,
				arrayType,
				builders.CppInitParams);
			
			// C++ init body
			AppendCppInitBody(
				funcName,
				funcNameLower,
				builders.CppInitBody);
			
			// C++ method declaration
			AppendIndent(
				indent + 1,
				builders.CppTypeDefinitions);
			AppendCppMethodDeclaration(
				"GetLength",
				false,
				false,
				false,
				typeof(int),
				null,
				parameters,
				builders.CppTypeDefinitions);
			
			// C++ method definition
			Type[] cppTypeParams = { elementType };
			AppendCppMethodDefinitionBegin(
				cppArrayTypeName,
				typeof(int),
				"GetLength",
				cppTypeParams,
				null,
				parameters,
				indent,
				builders.CppMethodDefinitions);
			AppendIndent(
				indent,
				builders.CppMethodDefinitions);
			builders.CppMethodDefinitions.Append("{\n");
			AppendIndent(
				indent + 1,
				builders.CppMethodDefinitions);
			builders.CppMethodDefinitions.Append("int32_t length = InternalLengths[dimension];\n");
			AppendIndent(
				indent + 1,
				builders.CppMethodDefinitions);
			builders.CppMethodDefinitions.Append("if (length)\n");
			AppendIndent(
				indent + 1,
				builders.CppMethodDefinitions);
			builders.CppMethodDefinitions.Append("{\n");
			AppendIndent(
				indent + 2,
				builders.CppMethodDefinitions);
			builders.CppMethodDefinitions.Append("return length;\n");
			AppendIndent(
				indent + 1,
				builders.CppMethodDefinitions);
			builders.CppMethodDefinitions.Append("}\n");
			AppendCppPluginFunctionCall(
				false,
				cppArrayTypeName,
				"System",
				TypeKind.Class,
				cppTypeParams,
				typeof(int),
				funcName,
				parameters,
				indent + 1,
				builders.CppMethodDefinitions);
			AppendIndent(
				indent + 1,
				builders.CppMethodDefinitions);
			builders.CppMethodDefinitions.Append(
				"InternalLengths[dimension] = returnValue;\n");
			AppendCppMethodReturn(
				typeof(int),
				TypeKind.Primitive,
				indent + 1,
				builders.CppMethodDefinitions);
			AppendIndent(
				indent,
				builders.CppMethodDefinitions);
			builders.CppMethodDefinitions.Append("}\n");
			AppendIndent(
				indent,
				builders.CppMethodDefinitions);
			builders.CppMethodDefinitions.Append('\n');
		}
		
		static void AppendArrayGetItem(
			Type elementType,
			TypeKind elementTypeKind,
			Type arrayType,
			string cppArrayTypeName,
			int rank,
			StringBuilders builders)
		{
			builders.TempStrBuilder.Length = 0;
			AppendArrayGetItemFuncName(
				elementType.Name,
				elementType.Namespace,
				cppArrayTypeName,
				rank,
				builders.TempStrBuilder);
			string funcName = builders.TempStrBuilder.ToString();
			
			builders.TempStrBuilder[0] = char.ToLower(
				builders.TempStrBuilder[0]);
			string funcNameLower = builders.TempStrBuilder.ToString();
			
			ParameterInfo[] parameters = BuildArrayGetItemsParams(
				rank,
				"index");
			
			// C# Delegate Type
			AppendCsharpDelegateType(
				funcName,
				false,
				arrayType,
				TypeKind.Class,
				elementType,
				parameters,
				builders.CsharpDelegateTypes);
			
			// C# Init Call
			AppendCsharpInitCallArg(
				funcName,
				builders.CsharpInitCall);
			
			// C# Init Param
			AppendCsharpInitParam(
				funcNameLower,
				builders.CsharpInitParams);
			
			// C# function
			AppendCsharpFunctionBeginning(
				arrayType,
				funcName,
				false,
				TypeKind.Class,
				elementType,
				parameters,
				builders.CsharpFunctions);
			builders.CsharpFunctions.Append("thiz[");
			for (int i = 0; i < rank; ++i)
			{
				builders.CsharpFunctions.Append("index");
				builders.CsharpFunctions.Append(i);
				if (i != rank-1)
				{
					builders.CsharpFunctions.Append(", ");
				}
			}
			builders.CsharpFunctions.Append("];");
			AppendCsharpFunctionReturn(
				parameters,
				elementType,
				elementTypeKind,
				null,
				false,
				builders.CsharpFunctions);
			
			// C++ function pointer definition
			AppendCppFunctionPointerDefinition(
				funcName,
				false,
				cppArrayTypeName,
				"System",
				TypeKind.Class,
				parameters,
				elementType,
				builders.CppFunctionPointers);
			
			// C++ init param
			AppendCppInitParam(
				funcNameLower,
				false,
				cppArrayTypeName,
				"System",
				TypeKind.Class,
				parameters,
				elementType,
				builders.CppInitParams);
			
			// C++ init body
			AppendCppInitBody(
				funcName,
				funcNameLower,
				builders.CppInitBody);
		}
		
		static void AppendArraySetItem(
			Type elementType,
			Type arrayType,
			string cppArrayTypeName,
			int rank,
			StringBuilders builders)
		{
			builders.TempStrBuilder.Length = 0;
			AppendArraySetItemFuncName(
				elementType.Name,
				elementType.Namespace,
				cppArrayTypeName,
				rank,
				builders.TempStrBuilder);
			string funcName = builders.TempStrBuilder.ToString();
			
			builders.TempStrBuilder[0] = char.ToLower(
				builders.TempStrBuilder[0]);
			string funcNameLower = builders.TempStrBuilder.ToString();
			
			// Build parameters as indexes then element
			ParameterInfo[] parameters = BuildArraySetItemsParams(
				rank,
				"index",
				elementType);
			
			// C# Delegate Type
			AppendCsharpDelegateType(
				funcName,
				false,
				arrayType,
				TypeKind.Class,
				typeof(void),
				parameters,
				builders.CsharpDelegateTypes);
			
			// C# Init Call
			AppendCsharpInitCallArg(
				funcName,
				builders.CsharpInitCall);
			
			// C# Init Param
			AppendCsharpInitParam(
				funcNameLower,
				builders.CsharpInitParams);
			
			// C# function
			AppendCsharpFunctionBeginning(
				arrayType,
				funcName,
				false,
				TypeKind.Class,
				typeof(void),
				parameters,
				builders.CsharpFunctions);
			builders.CsharpFunctions.Append("thiz[");
			for (int i = 0; i < rank; ++i)
			{
				builders.CsharpFunctions.Append("index");
				builders.CsharpFunctions.Append(i);
				if (i != rank-1)
				{
					builders.CsharpFunctions.Append(", ");
				}
			}
			builders.CsharpFunctions.Append("] = item;");
			AppendCsharpFunctionReturn(
				parameters,
				typeof(void),
				TypeKind.None,
				null,
				false,
				builders.CsharpFunctions);
			
			// C++ function pointer definition
			AppendCppFunctionPointerDefinition(
				funcName,
				false,
				cppArrayTypeName,
				"System",
				TypeKind.Class,
				parameters,
				arrayType,
				builders.CppFunctionPointers);
			
			// C++ init param
			AppendCppInitParam(
				funcNameLower,
				false,
				cppArrayTypeName,
				"System",
				TypeKind.Class,
				parameters,
				arrayType,
				builders.CppInitParams);
			
			// C++ init body
			AppendCppInitBody(
				funcName,
				funcNameLower,
				builders.CppInitBody);
		}
		
		static void AppendDelegate(
			JsonDelegate jsonDelegate,
			Assembly[] assemblies,
			StringBuilders builders)
		{
			Type type = GetType(
				jsonDelegate.Type,
				assemblies);
			Type[] genericArgTypes = type.GetGenericArguments();
			if (jsonDelegate.GenericParams != null)
			{
				foreach (JsonGenericParams jsonGenericParams
					in jsonDelegate.GenericParams)
				{
					// Build numbered C++ class name (e.g. Action2)
					builders.TempStrBuilder.Length = 0;
					AppendTypeNameWithoutSuffixes(
						type.Name,
						builders.TempStrBuilder);
					builders.TempStrBuilder.Append(
						jsonGenericParams.Types.Length);
					string numberedTypeName = builders.TempStrBuilder.ToString();
					
					// C++ template declaration
					AppendCppTemplateDeclaration(
						numberedTypeName,
						type.Namespace,
						genericArgTypes.Length,
						builders.CppTypeDeclarations);
				}
				
				foreach (JsonGenericParams jsonGenericParams
					in jsonDelegate.GenericParams)
				{
					Type[] typeParams = GetTypes(
						jsonGenericParams.Types,
						assemblies);
					Type genericType = type.MakeGenericType(typeParams);
					
					// Build numbered C++ class name (e.g. Action2)
					builders.TempStrBuilder.Length = 0;
					AppendTypeNameWithoutSuffixes(
						type.Name,
						builders.TempStrBuilder);
					builders.TempStrBuilder.Append(
						jsonGenericParams.Types.Length);
					string numberedTypeName = builders.TempStrBuilder.ToString();
					
					// Max simultaneous handles of this type
					int? maxSimultaneous = jsonGenericParams.MaxSimultaneous != 0
						? jsonGenericParams.MaxSimultaneous
						: jsonDelegate.MaxSimultaneous != 0
							? jsonDelegate.MaxSimultaneous
							: default(int?);
					
					AppendDelegate(
						genericType,
						numberedTypeName,
						typeParams,
						maxSimultaneous,
						builders);
				}
			}
			else
			{
				int? maxSimultaneous = jsonDelegate.MaxSimultaneous != 0
					? jsonDelegate.MaxSimultaneous
					: default(int?);
				AppendDelegate(
					type,
					type.Name,
					null,
					maxSimultaneous,
					builders);
			}
		}
		
		static void AppendDelegate(
			Type type,
			string numberedTypeName,
			Type[] typeParams,
			int? maxSimultaneous,
			StringBuilders builders)
		{
			builders.TempStrBuilder.Length = 0;
			AppendNamespace(
				type.Namespace,
				string.Empty,
				builders.TempStrBuilder);
			AppendTypeNameWithoutSuffixes(
				type.Name,
				builders.TempStrBuilder);
			AppendTypeNames(
				typeParams,
				builders.TempStrBuilder);
			string typeName = builders.TempStrBuilder.ToString();
			
			builders.TempStrBuilder.Length = 0;
			builders.TempStrBuilder.Append("Release");
			builders.TempStrBuilder.Append(typeName);
			string releaseFuncName = builders.TempStrBuilder.ToString();
			
			builders.TempStrBuilder[0] = char.ToLower(
				builders.TempStrBuilder[0]);
			string releaseFuncNameLower = builders.TempStrBuilder.ToString();
			
			builders.TempStrBuilder.Length = 0;
			builders.TempStrBuilder.Append(typeName);
			builders.TempStrBuilder.Append("Constructor");
			string constructorFuncName = builders.TempStrBuilder.ToString();
			
			builders.TempStrBuilder[0] = char.ToLower(
				builders.TempStrBuilder[0]);
			string constructorFuncNameLower = builders.TempStrBuilder.ToString();
			
			builders.TempStrBuilder.Length = 0;
			builders.TempStrBuilder.Append(typeName);
			builders.TempStrBuilder.Append("Add");
			string addFuncName = builders.TempStrBuilder.ToString();
			
			builders.TempStrBuilder[0] = char.ToLower(
				builders.TempStrBuilder[0]);
			string addFuncNameLower = builders.TempStrBuilder.ToString();
			
			builders.TempStrBuilder.Length = 0;
			builders.TempStrBuilder.Append(typeName);
			builders.TempStrBuilder.Append("Remove");
			string removeFuncName = builders.TempStrBuilder.ToString();
			
			builders.TempStrBuilder[0] = char.ToLower(
				builders.TempStrBuilder[0]);
			string removeFuncNameLower = builders.TempStrBuilder.ToString();
			
			// C++ type declaration
			int indent = AppendCppTypeDeclaration(
				type.Namespace,
				numberedTypeName,
				false,
				typeParams,
				builders.CppTypeDeclarations);
			
			ParameterInfo[] addRemoveParams = {
				new ParameterInfo
				{
					Name = "del",
					ParameterType = type,
					DereferencedParameterType = type,
					IsOut = false,
					IsRef = false,
					Kind = TypeKind.Class,
					IsVirtual = true
				}};
			
			ParameterInfo[] releaseParams = {
				new ParameterInfo
				{
					Name = "handle",
					ParameterType = typeof(int),
					DereferencedParameterType = typeof(int),
					IsOut = false,
					IsRef = false,
					Kind = TypeKind.Primitive
				},
				new ParameterInfo
				{
					Name = "classHandle",
					ParameterType = typeof(int),
					DereferencedParameterType = typeof(int),
					IsOut = false,
					IsRef = false,
					Kind = TypeKind.Primitive
				}};
			
			ParameterInfo[] constructorParams = {
				new ParameterInfo
				{
					Name = "cppHandle",
					ParameterType = typeof(int),
					DereferencedParameterType = typeof(int),
					IsOut = false,
					IsRef = false,
					Kind = TypeKind.Primitive
				},
				new ParameterInfo
				{
					Name = "handle",
					ParameterType = typeof(int),
					DereferencedParameterType = typeof(int),
					IsOut = true,
					IsRef = false,
					Kind = TypeKind.Primitive
				},
				new ParameterInfo
				{
					Name = "classHandle",
					ParameterType = typeof(int),
					DereferencedParameterType = typeof(int),
					IsOut = true,
					IsRef = false,
					Kind = TypeKind.Primitive
				}};
			
			AppendCppFreeListStateAndFunctions(
				type,
				typeName,
				builders.CppGlobalStateAndFunctions);

			AppendCppFreeListInit(
				type,
				maxSimultaneous,
				typeName,
				builders.CppInitBody);

			// C++ type definition (begin)
			AppendCppTypeDefinitionBegin(
				numberedTypeName,
				type.Namespace,
				TypeKind.Class,
				typeParams,
				"Object",
				"System",
				null,
				false,
				indent,
				builders.CppTypeDefinitions);
			
			// C++ type fields
			AppendIndent(
				indent + 1,
				builders.CppTypeDefinitions);
			builders.CppTypeDefinitions.Append("int32_t CppHandle;\n");
			AppendIndent(
				indent + 1,
				builders.CppTypeDefinitions);
			builders.CppTypeDefinitions.Append("int32_t ClassHandle;\n");
			
			// C++ method declarations
			AppendIndent(
				indent + 1,
				builders.CppTypeDefinitions);
			AppendCppMethodDeclaration(
				numberedTypeName,
				false,
				false,
				false,
				null,
				null,
				new ParameterInfo[0],
				builders.CppTypeDefinitions);
			AppendIndent(
				indent + 1,
				builders.CppTypeDefinitions);
			AppendCppMethodDeclaration(
				"operator+=",
				false,
				false,
				false,
				typeof(void),
				null,
				addRemoveParams,
				builders.CppTypeDefinitions);
			AppendIndent(
				indent + 1,
				builders.CppTypeDefinitions);
			AppendCppMethodDeclaration(
				"operator-=",
				false,
				false,
				false,
				typeof(void),
				null,
				addRemoveParams,
				builders.CppTypeDefinitions);
			
			// C++ function pointers
			AppendCppFunctionPointerDefinition(
				releaseFuncName,
				true,
				null,
				null,
				TypeKind.None,
				releaseParams,
				typeof(void),
				builders.CppFunctionPointers);
			AppendCppFunctionPointerDefinition(
				constructorFuncName,
				true,
				null,
				null,
				TypeKind.None,
				constructorParams,
				typeof(void),
				builders.CppFunctionPointers);
			AppendCppFunctionPointerDefinition(
				addFuncName,
				false,
				null,
				null,
				TypeKind.None,
				addRemoveParams,
				typeof(void),
				builders.CppFunctionPointers);
			AppendCppFunctionPointerDefinition(
				removeFuncName,
				false,
				null,
				null,
				TypeKind.None,
				addRemoveParams,
				typeof(void),
				builders.CppFunctionPointers);
			
			// C++ init params
			AppendCppInitParam(
				releaseFuncNameLower,
				true,
				null,
				null,
				TypeKind.None,
				releaseParams,
				typeof(void),
				builders.CppInitParams);
			AppendCppInitParam(
				constructorFuncNameLower,
				true,
				null,
				null,
				TypeKind.None,
				constructorParams,
				typeof(void),
				builders.CppInitParams);
			AppendCppInitParam(
				addFuncNameLower,
				false,
				null,
				null,
				TypeKind.None,
				addRemoveParams,
				typeof(void),
				builders.CppInitParams);
			AppendCppInitParam(
				removeFuncNameLower,
				false,
				null,
				null,
				TypeKind.None,
				addRemoveParams,
				typeof(void),
				builders.CppInitParams);
			
			// C++ and C# init params
			AppendCppInitBody(
				releaseFuncName,
				releaseFuncNameLower,
				builders.CppInitBody);
			AppendCppInitBody(
				constructorFuncName,
				constructorFuncNameLower,
				builders.CppInitBody);
			AppendCppInitBody(
				addFuncName,
				addFuncNameLower,
				builders.CppInitBody);
			AppendCppInitBody(
				removeFuncName,
				removeFuncNameLower,
				builders.CppInitBody);
			AppendCsharpInitParam(
				releaseFuncNameLower,
				builders.CsharpInitParams);
			AppendCsharpInitParam(
				constructorFuncNameLower,
				builders.CsharpInitParams);
			AppendCsharpInitParam(
				addFuncNameLower,
				builders.CsharpInitParams);
			AppendCsharpInitParam(
				removeFuncNameLower,
				builders.CsharpInitParams);
			AppendCsharpInitCallArg(
				releaseFuncName,
				builders.CsharpInitCall);
			AppendCsharpInitCallArg(
				constructorFuncName,
				builders.CsharpInitCall);
			AppendCsharpInitCallArg(
				addFuncName,
				builders.CsharpInitCall);
			AppendCsharpInitCallArg(
				removeFuncName,
				builders.CsharpInitCall);
			
			// C++ method definitions (end)
			int cppMethodDefinitionsIndent = AppendNamespaceBeginning(
				type.Namespace,
				builders.CppMethodDefinitions);
			
			AppendCppBaseTypeDefaultConstructor(
				typeName,
				numberedTypeName,
				typeParams,
				true,
				constructorFuncName,
				cppMethodDefinitionsIndent,
				builders.CppMethodDefinitions);
			
			AppendCppBaseTypeNullptrConstructor(
				typeName,
				numberedTypeName,
				typeParams,
				true,
				cppMethodDefinitionsIndent,
				builders.CppMethodDefinitions);
			
			AppendCppBaseTypeCopyConstructor(
				typeName,
				numberedTypeName,
				typeParams,
				true,
				cppMethodDefinitionsIndent,
				builders.CppMethodDefinitions);

			AppendCppBaseTypeMoveConstructor(
				numberedTypeName,
				typeParams,
				true,
				cppMethodDefinitionsIndent,
				builders.CppMethodDefinitions);

			AppendCppBaseTypeHandleConstructor(
				typeName,
				numberedTypeName,
				typeParams,
				true,
				cppMethodDefinitionsIndent,
				builders.CppMethodDefinitions);

			AppendCppBaseTypeDestructor(
				typeName,
				numberedTypeName,
				typeParams,
				true,
				releaseFuncName,
				cppMethodDefinitionsIndent,
				builders.CppMethodDefinitions);

			AppendCppBaseTypeAssignmentOperatorSameType(
				type,
				numberedTypeName,
				typeParams,
				true,
				cppMethodDefinitionsIndent,
				builders.CppMethodDefinitions);

			AppendCppBaseTypeAssignmentOperatorNullptr(
				numberedTypeName,
				typeParams,
				true,
				releaseFuncName,
				cppMethodDefinitionsIndent,
				builders.CppMethodDefinitions);

			AppendCppBaseTypeMoveAssignmentOperator(
				typeName,
				numberedTypeName,
				typeParams,
				true,
				releaseFuncName,
				cppMethodDefinitionsIndent,
				builders.CppMethodDefinitions);

			AppendCppBaseTypeEqualityOperator(
				numberedTypeName,
				typeParams,
				cppMethodDefinitionsIndent,
				builders.CppMethodDefinitions);

			AppendCppBaseTypeInequalityOperator(
				numberedTypeName,
				typeParams,
				cppMethodDefinitionsIndent,
				builders.CppMethodDefinitions);

			// C++ add
			AppendCppMethodDefinitionBegin(
				numberedTypeName,
				typeof(void),
				"operator+=",
				typeParams,
				null,
				addRemoveParams,
				indent,
				builders.CppMethodDefinitions);
			AppendIndent(
				indent,
				builders.CppMethodDefinitions);
			builders.CppMethodDefinitions.Append("{\n");
			AppendIndent(
				indent + 1,
				builders.CppMethodDefinitions);
			builders.CppMethodDefinitions.Append("Plugin::");
			builders.CppMethodDefinitions.Append(addFuncName);
			builders.CppMethodDefinitions.Append("(Handle, del.Handle);\n");
			AppendCppUnhandledExceptionHandling(
				indent + 1,
				builders.CppMethodDefinitions);
			AppendIndent(
				indent,
				builders.CppMethodDefinitions);
			builders.CppMethodDefinitions.Append("}\n");
			AppendIndent(
				indent,
				builders.CppMethodDefinitions);
			builders.CppMethodDefinitions.Append("\n");
			
			// C++ remove
			AppendCppMethodDefinitionBegin(
				numberedTypeName,
				typeof(void),
				"operator-=",
				typeParams,
				null,
				addRemoveParams,
				indent,
				builders.CppMethodDefinitions);
			AppendIndent(
				indent,
				builders.CppMethodDefinitions);
			builders.CppMethodDefinitions.Append("{\n");
			AppendIndent(
				indent + 1,
				builders.CppMethodDefinitions);
			builders.CppMethodDefinitions.Append("Plugin::");
			builders.CppMethodDefinitions.Append(removeFuncName);
			builders.CppMethodDefinitions.Append("(Handle, del.Handle);\n");
			AppendCppUnhandledExceptionHandling(
				indent + 1,
				builders.CppMethodDefinitions);
			AppendIndent(
				indent,
				builders.CppMethodDefinitions);
			builders.CppMethodDefinitions.Append("}\n");
			AppendIndent(
				indent,
				builders.CppMethodDefinitions);
			builders.CppMethodDefinitions.Append("\n");
			
			// C# GetDelegate call
			AppendCsharpGetDelegateCall(
				type.Name,
				type.Namespace,
				typeParams,
				"NativeInvoke",
				builders.CsharpGetDelegateCalls);
			
			// C# class (beginning)
			builders.CsharpBaseTypes.Append("\t\tclass ");
			builders.CsharpBaseTypes.Append(typeName);
			builders.CsharpBaseTypes.Append("\n");
			builders.CsharpBaseTypes.Append("\t\t{\n");
			
			// C# class fields
			builders.CsharpBaseTypes.Append("\t\t\tpublic int CppHandle;\n");
			builders.CsharpBaseTypes.Append("\t\t\tpublic ");
			AppendCsharpTypeName(
				type,
				builders.CsharpBaseTypes);
			builders.CsharpBaseTypes.Append(" Delegate;\n");
			builders.CsharpBaseTypes.Append("\t\t\t\n");
			
			// C# class constructor
			builders.CsharpBaseTypes.Append("\t\t\tpublic ");
			builders.CsharpBaseTypes.Append(typeName);
			builders.CsharpBaseTypes.Append("(int cppHandle)\n");
			builders.CsharpBaseTypes.Append("\t\t\t{\n");
			builders.CsharpBaseTypes.Append("\t\t\t\tCppHandle = cppHandle;\n");
			builders.CsharpBaseTypes.Append("\t\t\t\tDelegate = NativeInvoke;\n");
			builders.CsharpBaseTypes.Append("\t\t\t}\n");
			builders.CsharpBaseTypes.Append("\t\t\t\n");
			
			// operator() is how C# forwards the delegate invocation to C++
			AppendBaseTypeCppMethodCall(
				type,
				typeName,
				numberedTypeName,
				typeParams,
				type.GetMethod("Invoke"),
				"NativeInvoke",
				"operator()",
				false,
				indent,
				builders);
			
			// C# class (ending)
			builders.CsharpBaseTypes.Append("\t\t}\n");
			builders.CsharpBaseTypes.Append("\t\t\n");
			
			// Invoke() is how C++ invokes the delegate
			MethodInfo invokeMethod = type.GetMethod("Invoke");
			AppendBaseTypeMethodCallsCsharpMethod(
				type,
				typeName,
				numberedTypeName,
				typeParams,
				invokeMethod,
				"Invoke",
				null,
				indent,
				builders);
			
			// C# constructor delegate type
			AppendCsharpDelegateType(
				constructorFuncName,
				true,
				type,
				TypeKind.Class,
				typeof(void),
				constructorParams,
				builders.CsharpDelegateTypes);
			
			AppendCsharpBaseTypeConstructorFunction(
				type,
				typeName,
				false,
				constructorFuncName,
				constructorParams,
				builders.CsharpFunctions);

			// C# release delegate type
			AppendCsharpDelegateType(
				releaseFuncName,
				true,
				type,
				TypeKind.Class,
				typeof(void),
				releaseParams,
				builders.CsharpDelegateTypes);
			
			AppendCsharpBaseTypeReleaseFunction(
				type,
				typeName,
				true,
				releaseFuncName,
				releaseParams,
				builders.CsharpFunctions);

			// C# add delegate type
			AppendCsharpDelegateType(
				addFuncName,
				false,
				type,
				TypeKind.Class,
				typeof(void),
				addRemoveParams,
				builders.CsharpDelegateTypes);
			
			// C# add function
			AppendCsharpFunctionBeginning(
				type,
				addFuncName,
				false,
				TypeKind.Class,
				typeof(void),
				addRemoveParams,
				builders.CsharpFunctions);
			builders.CsharpFunctions.Append("thiz += del;");
			AppendCsharpFunctionReturn(
				addRemoveParams,
				typeof(void),
				TypeKind.Class,
				null,
				false,
				builders.CsharpFunctions);
			
			// C# remove delegate type
			AppendCsharpDelegateType(
				removeFuncName,
				false,
				type,
				TypeKind.Class,
				typeof(void),
				addRemoveParams,
				builders.CsharpDelegateTypes);
			
			// C# remove function
			AppendCsharpFunctionBeginning(
				type,
				removeFuncName,
				false,
				TypeKind.Class,
				typeof(void),
				addRemoveParams,
				builders.CsharpFunctions);
			builders.CsharpFunctions.Append("thiz -= del;");
			AppendCsharpFunctionReturn(
				addRemoveParams,
				typeof(void),
				TypeKind.Class,
				null,
				false,
				builders.CsharpFunctions);
			
			// C++ method definitions (end)
			AppendCppMethodDefinitionsEnd(
				indent,
				builders.CppMethodDefinitions);
			
			// C++ type definition (end)
			AppendCppTypeDefinitionEnd(
				false,
				indent,
				builders.CppTypeDefinitions);
		}
		
		static void AppendBaseType(
			Type type,
			JsonBaseType jsonBaseType,
			string numberedTypeName,
			Type[] typeParams,
			int? maxSimultaneous,
			StringBuilders builders)
		{
			builders.TempStrBuilder.Length = 0;
			AppendNamespace(
				type.Namespace,
				string.Empty,
				builders.TempStrBuilder);
			AppendTypeNameWithoutSuffixes(
				type.Name,
				builders.TempStrBuilder);
			AppendTypeNames(
				typeParams,
				builders.TempStrBuilder);
			string typeName = builders.TempStrBuilder.ToString();
			
			builders.TempStrBuilder.Length = 0;
			builders.TempStrBuilder.Append("Release");
			builders.TempStrBuilder.Append(typeName);
			string releaseFuncName = builders.TempStrBuilder.ToString();
			
			builders.TempStrBuilder[0] = char.ToLower(
				builders.TempStrBuilder[0]);
			string releaseFuncNameLower = builders.TempStrBuilder.ToString();
			
			builders.TempStrBuilder.Length = 0;
			builders.TempStrBuilder.Append(typeName);
			builders.TempStrBuilder.Append("Constructor");
			string constructorFuncName = builders.TempStrBuilder.ToString();
			
			builders.TempStrBuilder[0] = char.ToLower(
				builders.TempStrBuilder[0]);
			string constructorFuncNameLower = builders.TempStrBuilder.ToString();
			
			// C++ type declaration
			int indent = AppendCppTypeDeclaration(
				type.Namespace,
				numberedTypeName,
				false,
				typeParams,
				builders.CppTypeDeclarations);
			
			ParameterInfo[] releaseParams = {
				new ParameterInfo
				{
					Name = "handle",
					ParameterType = typeof(int),
					DereferencedParameterType = typeof(int),
					IsOut = false,
					IsRef = false,
					Kind = TypeKind.Primitive
				}};
			
			ParameterInfo[] constructorParams = {
				new ParameterInfo
				{
					Name = "cppHandle",
					ParameterType = typeof(int),
					DereferencedParameterType = typeof(int),
					IsOut = false,
					IsRef = false,
					Kind = TypeKind.Primitive
				},
				new ParameterInfo
				{
					Name = "handle",
					ParameterType = typeof(int),
					DereferencedParameterType = typeof(int),
					IsOut = true,
					IsRef = false,
					Kind = TypeKind.Primitive
				}};
			
			AppendCppFreeListStateAndFunctions(
				type,
				typeName,
				builders.CppGlobalStateAndFunctions);

			AppendCppFreeListInit(
				type,
				maxSimultaneous,
				typeName,
				builders.CppInitBody);

			// C++ type definition (begin)
			AppendCppTypeDefinitionBegin(
				numberedTypeName,
				type.Namespace,
				TypeKind.Class,
				typeParams,
				"Object",
				"System",
				null,
				false,
				indent,
				builders.CppTypeDefinitions);
			
			// C++ type fields
			AppendIndent(
				indent + 1,
				builders.CppTypeDefinitions);
			builders.CppTypeDefinitions.Append("int32_t CppHandle;\n");
			
			// C++ method declarations
			AppendIndent(
				indent + 1,
				builders.CppTypeDefinitions);
			AppendCppMethodDeclaration(
				numberedTypeName,
				false,
				false,
				false,
				null,
				null,
				new ParameterInfo[0],
				builders.CppTypeDefinitions);
			
			// C++ function pointers
			AppendCppFunctionPointerDefinition(
				releaseFuncName,
				true,
				null,
				null,
				TypeKind.None,
				releaseParams,
				typeof(void),
				builders.CppFunctionPointers);
			AppendCppFunctionPointerDefinition(
				constructorFuncName,
				true,
				null,
				null,
				TypeKind.None,
				constructorParams,
				typeof(void),
				builders.CppFunctionPointers);
			
			// C++ init params
			AppendCppInitParam(
				releaseFuncNameLower,
				true,
				null,
				null,
				TypeKind.None,
				releaseParams,
				typeof(void),
				builders.CppInitParams);
			AppendCppInitParam(
				constructorFuncNameLower,
				true,
				null,
				null,
				TypeKind.None,
				constructorParams,
				typeof(void),
				builders.CppInitParams);
			
			// C++ and C# init params
			AppendCppInitBody(
				releaseFuncName,
				releaseFuncNameLower,
				builders.CppInitBody);
			AppendCppInitBody(
				constructorFuncName,
				constructorFuncNameLower,
				builders.CppInitBody);
			AppendCsharpInitParam(
				releaseFuncNameLower,
				builders.CsharpInitParams);
			AppendCsharpInitParam(
				constructorFuncNameLower,
				builders.CsharpInitParams);
			AppendCsharpInitCallArg(
				releaseFuncName,
				builders.CsharpInitCall);
			AppendCsharpInitCallArg(
				constructorFuncName,
				builders.CsharpInitCall);
			
			// C++ method definitions (end)
			int cppMethodDefinitionsIndent = AppendNamespaceBeginning(
				type.Namespace,
				builders.CppMethodDefinitions);
			
			AppendCppBaseTypeDefaultConstructor(
				typeName,
				numberedTypeName,
				typeParams,
				false,
				constructorFuncName,
				cppMethodDefinitionsIndent,
				builders.CppMethodDefinitions);
			
			AppendCppBaseTypeNullptrConstructor(
				typeName,
				numberedTypeName,
				typeParams,
				false,
				cppMethodDefinitionsIndent,
				builders.CppMethodDefinitions);
			
			AppendCppBaseTypeCopyConstructor(
				typeName,
				numberedTypeName,
				typeParams,
				false,
				cppMethodDefinitionsIndent,
				builders.CppMethodDefinitions);

			AppendCppBaseTypeMoveConstructor(
				numberedTypeName,
				typeParams,
				false,
				cppMethodDefinitionsIndent,
				builders.CppMethodDefinitions);

			AppendCppBaseTypeHandleConstructor(
				typeName,
				numberedTypeName,
				typeParams,
				false,
				cppMethodDefinitionsIndent,
				builders.CppMethodDefinitions);

			AppendCppBaseTypeDestructor(
				typeName,
				numberedTypeName,
				typeParams,
				false,
				releaseFuncName,
				cppMethodDefinitionsIndent,
				builders.CppMethodDefinitions);

			AppendCppBaseTypeAssignmentOperatorSameType(
				type,
				numberedTypeName,
				typeParams,
				false,
				cppMethodDefinitionsIndent,
				builders.CppMethodDefinitions);

			AppendCppBaseTypeAssignmentOperatorNullptr(
				numberedTypeName,
				typeParams,
				false,
				releaseFuncName,
				cppMethodDefinitionsIndent,
				builders.CppMethodDefinitions);

			AppendCppBaseTypeMoveAssignmentOperator(
				typeName,
				numberedTypeName,
				typeParams,
				false,
				releaseFuncName,
				cppMethodDefinitionsIndent,
				builders.CppMethodDefinitions);

			AppendCppBaseTypeEqualityOperator(
				numberedTypeName,
				typeParams,
				cppMethodDefinitionsIndent,
				builders.CppMethodDefinitions);

			AppendCppBaseTypeInequalityOperator(
				numberedTypeName,
				typeParams,
				cppMethodDefinitionsIndent,
				builders.CppMethodDefinitions);
			
			// C# class (beginning)
			builders.CsharpBaseTypes.Append("\t\tclass ");
			builders.CsharpBaseTypes.Append(typeName);
			if (jsonBaseType != null)
			{
				builders.CsharpBaseTypes.Append(" : ");
				AppendCsharpTypeName(
					type,
					builders.CsharpBaseTypes);
			}
			builders.CsharpBaseTypes.Append("\n");
			builders.CsharpBaseTypes.Append("\t\t{\n");
			
			// C# class fields
			builders.CsharpBaseTypes.Append("\t\t\tpublic int CppHandle;\n");
			builders.CsharpBaseTypes.Append("\t\t\t\n");
			
			// C# class constructor
			builders.CsharpBaseTypes.Append("\t\t\tpublic ");
			builders.CsharpBaseTypes.Append(typeName);
			builders.CsharpBaseTypes.Append("(int cppHandle)\n");
			builders.CsharpBaseTypes.Append("\t\t\t{\n");
			builders.CsharpBaseTypes.Append("\t\t\t\tCppHandle = cppHandle;\n");
			builders.CsharpBaseTypes.Append("\t\t\t}\n");
			builders.CsharpBaseTypes.Append("\t\t\t\n");
			
			// C# constructor delegate type
			AppendCsharpDelegateType(
				constructorFuncName,
				true,
				type,
				TypeKind.Class,
				typeof(void),
				constructorParams,
				builders.CsharpDelegateTypes);
			
			AppendCsharpBaseTypeConstructorFunction(
				type,
				typeName,
				false,
				constructorFuncName,
				constructorParams,
				builders.CsharpFunctions);

			// C# release delegate type
			AppendCsharpDelegateType(
				releaseFuncName,
				true,
				type,
				TypeKind.Class,
				typeof(void),
				releaseParams,
				builders.CsharpDelegateTypes);
			
			AppendCsharpBaseTypeReleaseFunction(
				type,
				typeName,
				false,
				releaseFuncName,
				releaseParams,
				builders.CsharpFunctions);
			
			// All abstract methods
			foreach (MethodInfo methodInfo in type.GetMethods())
			{
				if (methodInfo.IsAbstract)
				{
					AppendBaseTypeNativeMethod(
						type,
						typeName,
						typeParams,
						numberedTypeName,
						methodInfo,
						indent,
						builders);
				}
			}
			
			// Specified virtual methods
			if (jsonBaseType.OverrideMethods != null)
			{
				MethodInfo[] methods = type.GetMethods();
				Type[] genericArgTypes = type.GetGenericArguments();
				foreach (JsonMethod jsonMethod in jsonBaseType.OverrideMethods)
				{
					MethodInfo methodInfo = GetMethod(
						jsonMethod,
						type,
						typeParams,
						genericArgTypes,
						methods);
					AppendBaseTypeNativeMethod(
						type,
						typeName,
						typeParams,
						numberedTypeName,
						methodInfo,
						indent,
						builders);
				}
			}
			
			// C# class (ending)
			builders.CsharpBaseTypes.Append("\t\t}\n");
			builders.CsharpBaseTypes.Append("\t\t\n");
			
			// C++ method definitions (end)
			AppendCppMethodDefinitionsEnd(
				indent,
				builders.CppMethodDefinitions);
			
			// C++ type definition (end)
			AppendCppTypeDefinitionEnd(
				false,
				indent,
				builders.CppTypeDefinitions);
		}
		
		static void AppendBaseTypeNativeMethod(
			Type type,
			string typeName,
			Type[] typeParams,
			string cppTypeName,
			MethodInfo methodInfo,
			int indent,
			StringBuilders builders)
		{
			AppendCsharpGetDelegateCall(
				type.Name,
				type.Namespace,
				typeParams,
				methodInfo.Name,
				builders.CsharpGetDelegateCalls);
			
			AppendBaseTypeCppMethodCall(
				type,
				typeName,
				cppTypeName,
				typeParams,
				methodInfo,
				methodInfo.Name,
				methodInfo.Name,
				!type.IsInterface,
				indent,
				builders);
		}

		static void AppendBaseTypeMethodCallsCsharpMethod(
			Type type,
			string typeName,
			string cppTypeName,
			Type[] typeParams,
			MethodInfo methodInfo,
			string methodName,
			string csharpMethodName,
			int indent,
			StringBuilders builders)
		{
			builders.TempStrBuilder.Length = 0;
			builders.TempStrBuilder.Append(typeName);
			builders.TempStrBuilder.Append(methodName);
			string funcName = builders.TempStrBuilder.ToString();
			
			builders.TempStrBuilder[0] = char.ToLower(
				builders.TempStrBuilder[0]);
			string funcNameLower = builders.TempStrBuilder.ToString();
			
			// C++ method declaration for the method
			ParameterInfo[] invokeParams = ConvertParameters(
				methodInfo.GetParameters());
			AppendIndent(
				indent + 1,
				builders.CppTypeDefinitions);
			AppendCppMethodDeclaration(
				methodName,
				false,
				false,
				false,
				methodInfo.ReturnType,
				null,
				invokeParams,
				builders.CppTypeDefinitions);
			
			// C++ function pointer for the C# binding function
			AppendCppFunctionPointerDefinition(
				funcName,
				false,
				null,
				null,
				TypeKind.None,
				invokeParams,
				methodInfo.ReturnType,
				builders.CppFunctionPointers);
			
			// C++ and C# Init parameter and body for the C# binding function
			AppendCppInitParam(
				funcNameLower,
				false,
				null,
				null,
				TypeKind.None,
				invokeParams,
				methodInfo.ReturnType,
				builders.CppInitParams);
			AppendCppInitBody(
				funcName,
				funcNameLower,
				builders.CppInitBody);
			AppendCsharpInitParam(
				funcNameLower,
				builders.CsharpInitParams);
			AppendCsharpInitCallArg(
				funcName,
				builders.CsharpInitCall);
			
			// C++ method definition for the method
			TypeKind returnTypeKind = GetTypeKind(
				methodInfo.ReturnType);
			AppendCppMethodDefinitionBegin(
				cppTypeName,
				methodInfo.ReturnType,
				methodName,
				typeParams,
				null,
				invokeParams,
				indent,
				builders.CppMethodDefinitions);
			AppendIndent(
				indent,
				builders.CppMethodDefinitions);
			builders.CppMethodDefinitions.Append("{\n");
			AppendCppPluginFunctionCall(
				false,
				type.Name,
				type.Namespace,
				TypeKind.Class,
				typeParams,
				methodInfo.ReturnType,
				funcName,
				invokeParams,
				indent + 1,
				builders.CppMethodDefinitions);
			AppendCppMethodReturn(
				methodInfo.ReturnType,
				returnTypeKind,
				indent + 1,
				builders.CppMethodDefinitions);
			AppendIndent(
				indent,
				builders.CppMethodDefinitions);
			builders.CppMethodDefinitions.Append("}\n");
			AppendIndent(
				indent,
				builders.CppMethodDefinitions);
			builders.CppMethodDefinitions.Append('\n');
			
			// C# delegate type for the binding function that C++ calls
			ParameterInfo[] invokeParamsWithThis = new ParameterInfo[
				invokeParams.Length + 1];
			for (int i = 0; i < invokeParams.Length; ++i)
			{
				invokeParamsWithThis[i+1] = invokeParams[i];
			}
			invokeParamsWithThis[0] = new ParameterInfo {
				Name = "thisHandle",
				ParameterType = typeof(int),
				DereferencedParameterType = typeof(int),
				IsOut = false,
				IsRef = false,
				Kind = TypeKind.Primitive
			};
			AppendCsharpDelegateType(
				funcName,
				true,
				type,
				TypeKind.Class,
				methodInfo.ReturnType,
				invokeParamsWithThis,
				builders.CsharpDelegateTypes);
			
			// C# binding function that C++ calls to invoke the method
			AppendCsharpFunctionBeginning(
				type,
				funcName,
				true,
				TypeKind.Class,
				methodInfo.ReturnType,
				invokeParamsWithThis,
				builders.CsharpFunctions);
			builders.CsharpFunctions.Append("((");
			AppendCsharpTypeName(
				type,
				builders.CsharpFunctions);
			builders.CsharpFunctions.Append(
				")NativeScript.Bindings.ObjectStore.Get(thisHandle))");
			if (csharpMethodName != null)
			{
				builders.CsharpFunctions.Append('.');
				builders.CsharpFunctions.Append(csharpMethodName);
			}
			AppendCsharpFunctionCallParameters(
				invokeParams,
				builders.CsharpFunctions);
			builders.CsharpFunctions.Append(';');
			AppendCsharpFunctionReturn(
				invokeParams,
				methodInfo.ReturnType,
				returnTypeKind,
				null,
				false,
				builders.CsharpFunctions);
		}
		
		static void AppendBaseTypeCppMethodCall(
			Type type,
			string typeName,
			string numberedTypeName,
			Type[] typeParams,
			MethodInfo invokeMethod,
			string funcName,
			string methodName,
			bool isOverride,
			int indent,
			StringBuilders builders)
		{
			// Build the name of the C++ binding function that C# calls
			builders.TempStrBuilder.Length = 0;
			AppendNamespace(
				type.Namespace,
				string.Empty,
				builders.TempStrBuilder);
			AppendTypeNameWithoutSuffixes(
				type.Name,
				builders.TempStrBuilder);
			AppendTypeNames(
				typeParams,
				builders.TempStrBuilder);
			builders.TempStrBuilder.Append(funcName);
			string nativeInvokeFuncName = builders.TempStrBuilder.ToString();
			
			// C++ method declaration
			ParameterInfo[] invokeParams = ConvertParameters(
				invokeMethod.GetParameters());
			AppendIndent(
				indent + 1,
				builders.CppTypeDefinitions);
			AppendCppMethodDeclaration(
				methodName,
				false,
				true,
				false,
				invokeMethod.ReturnType,
				null,
				invokeParams,
				builders.CppTypeDefinitions);
			
			// C++ method definition. This is a no-op that game code overrides.
			AppendCppMethodDefinitionBegin(
				numberedTypeName,
				invokeMethod.ReturnType,
				methodName,
				typeParams,
				null,
				invokeParams,
				indent,
				builders.CppMethodDefinitions);
			AppendIndent(
				indent,
				builders.CppMethodDefinitions);
			builders.CppMethodDefinitions.Append("{\n");
			if (invokeMethod.ReturnType != typeof(void))
			{
				AppendIndent(
					indent + 1,
					builders.CppMethodDefinitions);
				builders.CppMethodDefinitions.Append("return {};\n");
			}
			AppendIndent(
				indent,
				builders.CppMethodDefinitions);
			builders.CppMethodDefinitions.Append("}\n");
			AppendIndent(
				indent,
				builders.CppMethodDefinitions);
			builders.CppMethodDefinitions.Append('\n');
			
			// C++ binding function that C# calls. Calls the C++ method.
			TypeKind invokeReturnTypeKind = GetTypeKind(
				invokeMethod.ReturnType);
			AppendCppBaseTypeMethodInvokeBindingFunction(
				funcName,
				type,
				typeParams,
				invokeMethod,
				methodName,
				invokeReturnTypeKind,
				invokeParams,
				indent,
				typeName,
				builders.CppMethodDefinitions);
			
			// C# method that calls the C++ binding function
			ParameterInfo[] invokeParamsWithThis = new ParameterInfo[
				invokeParams.Length + 1];
			for (int i = 0; i < invokeParams.Length; ++i)
			{
				invokeParamsWithThis[i+1] = invokeParams[i];
			}
			invokeParamsWithThis[0] = new ParameterInfo {
				Name = "thisHandle",
				ParameterType = typeof(int),
				DereferencedParameterType = typeof(int),
				IsOut = false,
				IsRef = false,
				Kind = TypeKind.Primitive
			};
			AppendCsharpBaseTypeCppMethodCallMethod(
				isOverride,
				invokeMethod,
				funcName,
				invokeParams,
				nativeInvokeFuncName,
				invokeParamsWithThis,
				invokeReturnTypeKind,
				builders.CsharpBaseTypes);
			
			// C# delegate for the C++ binding function
			AppendCsharpDelegate(
				false,
				type.Name,
				type.Namespace,
				typeParams,
				funcName,
				invokeParams,
				invokeMethod.ReturnType,
				invokeReturnTypeKind,
				builders.CsharpDelegates);
			
			// C# import for the C++ binding function
			AppendCsharpImport(
				type.Name,
				type.Namespace,
				typeParams,
				funcName,
				invokeParams,
				builders.CsharpImports);
		}

		static void AppendCsharpBaseTypeReleaseFunction(
			Type type,
			string typeName,
			bool typeIsDelegate,
			string releaseFuncName,
			ParameterInfo[] releaseParams,
			StringBuilder output)
		{
			AppendCsharpFunctionBeginning(
				type,
				releaseFuncName,
				true,
				TypeKind.Class,
				typeof(void),
				releaseParams,
				output);
			if (typeIsDelegate)
			{
				output.Append("if (classHandle != 0)\n");
				output.Append("\t\t\t\t{\n");
				output.Append("\t\t\t\t\tvar thiz = (");
				output.Append(typeName);
				output.Append(
					")NativeScript.Bindings.ObjectStore.Remove(classHandle);\n");
				output.Append("\t\t\t\t\tthiz.CppHandle = 0;\n");
				output.Append("\t\t\t\t}\n");
				output.Append("\t\t\t\t");
			}
			output.Append(
				"NativeScript.Bindings.ObjectStore.Remove(handle);");
			AppendCsharpFunctionReturn(
				releaseParams,
				typeof(void),
				TypeKind.Class,
				null,
				true,
				output);
		}

		static void AppendCsharpBaseTypeCppMethodCallMethod(
			bool isOverride,
			MethodInfo invokeMethod,
			string funcName,
			ParameterInfo[] invokeParams,
			string nativeInvokeFuncName,
			ParameterInfo[] invokeParamsWithThis,
			TypeKind invokeReturnTypeKind,
			StringBuilder output)
		{
			output.Append("\t\t\tpublic ");
			if (isOverride)
			{
				output.Append("override ");
			}
			AppendCsharpTypeName(
				invokeMethod.ReturnType,
				output);
			output.Append(" ");
			output.Append(funcName);
			output.Append("(");
			for (int i = 0; i < invokeParams.Length; ++i)
			{
				ParameterInfo param = invokeParams[i];
				AppendCsharpTypeName(
					param.ParameterType,
					output);
				output.Append(' ');
				output.Append(param.Name);
				if (i != invokeParams.Length - 1)
				{
					output.Append(", ");
				}
			}
			output.Append(")\n");
			output.Append("\t\t\t{\n");
			output.Append("\t\t\t\tif (CppHandle != 0)\n");
			output.Append("\t\t\t\t{\n");
			output.Append("\t\t\t\t\tint thisHandle = CppHandle;\n");
			AppendCppFunctionCall(
				nativeInvokeFuncName,
				invokeParamsWithThis,
				invokeMethod.ReturnType,
				true,
				5,
				output);
			if (invokeMethod.ReturnType != typeof(void))
			{
				output.Append("\t\t\t\t\treturn ");
				switch (invokeReturnTypeKind)
				{
					case TypeKind.Class:
					case TypeKind.ManagedStruct:
						if (invokeMethod.ReturnType != typeof(object))
						{
							output.Append('(');
							AppendCsharpTypeName(
								invokeMethod.ReturnType,
								output);
							output.Append(')');
						}
						AppendHandleStoreTypeName(
							invokeMethod.ReturnType,
							output);
						output.Append(".Get(returnVal);\n");
						break;
					default:
						output.Append("returnVal;\n");
						break;
				}
			}
			output.Append("\t\t\t\t}\n");
			if (invokeMethod.ReturnType != typeof(void))
			{
				output.Append("\t\t\t\treturn default(");
				AppendCsharpTypeName(
					invokeMethod.ReturnType,
					output);
				output.Append(");\n");
			}
			output.Append("\t\t\t}\n");
		}

		private static void AppendCsharpBaseTypeConstructorFunction(
			Type type,
			string typeName,
			bool typeIsDelegate,
			string constructorFuncName,
			ParameterInfo[] constructorParams,
			StringBuilder output)
		{
			AppendCsharpFunctionBeginning(
				type,
				constructorFuncName,
				true,
				TypeKind.Class,
				typeof(void),
				constructorParams,
				output);
			output.Append("var thiz = new ");
			output.Append(typeName);
			output.Append("(cppHandle);\n");
			if (typeIsDelegate)
			{
				output.Append(
					"\t\t\t\tclassHandle = NativeScript.Bindings.ObjectStore.Store(thiz);\n");
				output.Append(
					"\t\t\t\thandle = NativeScript.Bindings.ObjectStore.Store(thiz.Delegate);");
			}
			else
			{
				output.Append(
					"\t\t\t\thandle = NativeScript.Bindings.ObjectStore.Store(thiz);");
			}
			AppendCsharpFunctionReturn(
				constructorParams,
				typeof(void),
				TypeKind.Class,
				null,
				true,
				output);
		}

		static void AppendCppBaseTypeMethodInvokeBindingFunction(
			string funcName,
			Type type,
			Type[] typeParams,
			MethodInfo method,
			string methodName,
			TypeKind methodReturnTypeKind,
			ParameterInfo[] methodParams,
			int indent,
			string typeName,
			StringBuilder output)
		{
			AppendIndent(
				indent,
				output);
			output.Append("DLLEXPORT ");
			if (method.ReturnType == typeof(void))
			{
				output.Append("void");
			}
			else if (method.ReturnType == typeof(bool))
			{
				// C linkage requires us to use primitive types
				output.Append("int32_t");
			}
			else if (method.ReturnType == typeof(char))
			{
				// C linkage requires us to use primitive types
				output.Append("int16_t");
			}
			else
			{
				switch (methodReturnTypeKind)
				{
					case TypeKind.Class:
					case TypeKind.ManagedStruct:
						output.Append("int32_t");
						break;
					default:
						AppendCppTypeName(
							method.ReturnType,
							output);
						break;
				}
			}
			output.Append(' ');
			AppendCsharpDelegateName(
				type.Name,
				type.Namespace,
				typeParams,
				funcName,
				output);
			output.Append("(int32_t cppHandle");
			if (methodParams.Length > 0)
			{
				output.Append(", ");
			}
			for (int i = 0; i < methodParams.Length; ++i)
			{
				ParameterInfo param = methodParams[i];
				switch (param.Kind)
				{
					case TypeKind.Class:
					case TypeKind.ManagedStruct:
						output.Append("int32_t ");
						output.Append(param.Name);
						output.Append("Handle");
						break;
					default:
						AppendCppTypeName(
							param.ParameterType,
							output);
						output.Append(' ');
						output.Append(param.Name);
						break;
				}
				if (i != methodParams.Length - 1)
				{
					output.Append(", ");
				}
			}
			output.Append(")\n");
			AppendIndent(
				indent,
				output);
			output.Append("{\n");
			AppendIndent(
				indent + 1,
				output);
			output.Append("try\n");
			AppendIndent(
				indent + 1,
				output);
			output.Append("{\n");
			for (int i = 0; i < methodParams.Length; ++i)
			{
				ParameterInfo parameter = methodParams[i];
				if (parameter.Kind == TypeKind.Class ||
				    parameter.Kind == TypeKind.ManagedStruct)
				{
					AppendIndent(
						indent + 2,
						output);
					output.Append("auto param");
					output.Append(i);
					output.Append(" = ");
					AppendCppTypeName(
						parameter.ParameterType,
						output);
					output.Append("(Plugin::InternalUse::Only, ");
					output.Append(parameter.Name);
					output.Append("Handle);\n");
				}
			}
			AppendIndent(
				indent + 2,
				output);
			if (method.ReturnType != typeof(void))
			{
				output.Append("return ");
			}
			output.Append("Plugin::Get");
			output.Append(typeName);
			output.Append("(cppHandle)->");
			output.Append(methodName);
			output.Append("(");
			for (int i = 0; i < methodParams.Length; ++i)
			{
				ParameterInfo parameter = methodParams[i];
				if (parameter.Kind == TypeKind.Class ||
				    parameter.Kind == TypeKind.ManagedStruct)
				{
					output.Append("param");
					output.Append(i);
				}
				else
				{
					output.Append(parameter.Name);
				}
				if (i != methodParams.Length - 1)
				{
					output.Append(", ");
				}
			}
			output.Append(")");
			if (
				method.ReturnType != typeof(void) &&
				(methodReturnTypeKind == TypeKind.Class ||
				 methodReturnTypeKind == TypeKind.ManagedStruct))
			{
				output.Append(".Handle");
			}
			output.Append(";\n");
			AppendIndent(
				indent + 1,
				output);
			output.Append("}\n");
			AppendIndent(
				indent + 1,
				output);
			output.Append(
				"catch (System::Exception ex)\n");
			AppendIndent(
				indent + 1,
				output);
			output.Append("{\n");
			AppendIndent(
				indent + 2,
				output);
			output.Append(
				"Plugin::SetException(ex.Handle);\n");
			if (method.ReturnType != typeof(void))
			{
				AppendIndent(
					indent + 2,
					output);
				output.Append(
					"return {};\n");
			}
			AppendIndent(
				indent + 1,
				output);
			output.Append("}\n");
			AppendIndent(
				indent + 1,
				output);
			output.Append("catch (...)\n");
			AppendIndent(
				indent + 1,
				output);
			output.Append("{\n");
			AppendIndent(
				indent + 2,
				output);
			output.Append(
				"System::String msg = \"Unhandled exception invoking ");
			AppendCppTypeName(
				type,
				output);
			output.Append("\";\n");
			AppendIndent(
				indent + 2,
				output);
			output.Append(
				"System::Exception ex(msg);\n");
			AppendIndent(
				indent + 2,
				output);
			output.Append(
				"Plugin::SetException(ex.Handle);\n");
			if (method.ReturnType != typeof(void))
			{
				AppendIndent(
					indent + 2,
					output);
				output.Append(
					"return {};\n");
			}
			AppendIndent(
				indent + 1,
				output);
			output.Append("}\n");
			AppendIndent(
				indent,
				output);
			output.Append("}\n");
			AppendIndent(
				indent,
				output);
			output.Append("\n");
		}

		static void AppendCppBaseTypeInequalityOperator(
			string numberedTypeName,
			Type[] typeParams,
			int cppMethodDefinitionsIndent,
			StringBuilder output)
		{
			AppendIndent(
				cppMethodDefinitionsIndent,
				output);
			output.Append("bool ");
			AppendTypeNameWithoutGenericSuffix(
				numberedTypeName,
				output);
			AppendCppTypeParameters(
				typeParams,
				output);
			output.Append("::operator!=(const ");
			AppendTypeNameWithoutGenericSuffix(
				numberedTypeName,
				output);
			AppendCppTypeParameters(
				typeParams,
				output);
			output.Append("& other) const\n");
			AppendIndent(
				cppMethodDefinitionsIndent,
				output);
			output.Append("{\n");
			AppendIndent(
				cppMethodDefinitionsIndent + 1,
				output);
			output.Append(
				"return Handle != other.Handle;\n");
			AppendIndent(
				cppMethodDefinitionsIndent,
				output);
			output.Append("}\n");
			AppendIndent(
				cppMethodDefinitionsIndent,
				output);
			output.Append('\n');
		}

		static void AppendCppBaseTypeEqualityOperator(
			string numberedTypeName,
			Type[] typeParams,
			int cppMethodDefinitionsIndent,
			StringBuilder output)
		{
			AppendIndent(
				cppMethodDefinitionsIndent,
				output);
			output.Append("bool ");
			AppendTypeNameWithoutGenericSuffix(
				numberedTypeName,
				output);
			AppendCppTypeParameters(
				typeParams,
				output);
			output.Append("::operator==(const ");
			AppendTypeNameWithoutGenericSuffix(
				numberedTypeName,
				output);
			AppendCppTypeParameters(
				typeParams,
				output);
			output.Append("& other) const\n");
			AppendIndent(
				cppMethodDefinitionsIndent,
				output);
			output.Append("{\n");
			AppendIndent(
				cppMethodDefinitionsIndent + 1,
				output);
			output.Append(
				"return Handle == other.Handle;\n");
			AppendIndent(
				cppMethodDefinitionsIndent,
				output);
			output.Append("}\n");
			AppendIndent(
				cppMethodDefinitionsIndent,
				output);
			output.Append('\n');
		}

		static void AppendCppBaseTypeMoveAssignmentOperator(
			string typeName,
			string numberedTypeName,
			Type[] typeParams,
			bool typeIsDelegate,
			string releaseFuncName,
			int cppMethodDefinitionsIndent,
			StringBuilder output)
		{
			AppendIndent(
				cppMethodDefinitionsIndent,
				output);
			AppendTypeNameWithoutGenericSuffix(
				numberedTypeName,
				output);
			AppendCppTypeParameters(
				typeParams,
				output);
			output.Append("& ");
			AppendTypeNameWithoutGenericSuffix(
				numberedTypeName,
				output);
			AppendCppTypeParameters(
				typeParams,
				output);
			output.Append("::operator=(");
			AppendTypeNameWithoutGenericSuffix(
				numberedTypeName,
				output);
			AppendCppTypeParameters(
				typeParams,
				output);
			output.Append("&& other)\n");
			AppendIndent(
				cppMethodDefinitionsIndent,
				output);
			output.Append("{\n");
			AppendIndent(
				cppMethodDefinitionsIndent + 1,
				output);
			output.Append("Plugin::Remove");
			output.Append(typeName);
			output.Append("(CppHandle);\n");
			AppendIndent(
				cppMethodDefinitionsIndent + 1,
				output);
			output.Append("CppHandle = 0;\n");
			AppendIndent(
				cppMethodDefinitionsIndent + 1,
				output);
			output.Append("if (Handle)\n");
			AppendIndent(
				cppMethodDefinitionsIndent + 1,
				output);
			output.Append("{\n");
			AppendIndent(
				cppMethodDefinitionsIndent + 2,
				output);
			output.Append("int32_t handle = Handle;\n");
			if (typeIsDelegate)
			{
				AppendIndent(
					cppMethodDefinitionsIndent + 2,
					output);
				output.Append("int32_t classHandle = ClassHandle;\n");
			}
			AppendIndent(
				cppMethodDefinitionsIndent + 2,
				output);
			output.Append("Handle = 0;\n");
			if (typeIsDelegate)
			{
				AppendIndent(
					cppMethodDefinitionsIndent + 2,
					output);
				output.Append("ClassHandle = 0;\n");
			}
			AppendIndent(
				cppMethodDefinitionsIndent + 2,
				output);
			output.Append(
				"if (Plugin::DereferenceManagedClassNoRelease(handle))\n");
			AppendIndent(
				cppMethodDefinitionsIndent + 2,
				output);
			output.Append("{\n");
			AppendIndent(
				cppMethodDefinitionsIndent + 3,
				output);
			output.Append("Plugin::");
			output.Append(releaseFuncName);
			output.Append("(handle");
			if (typeIsDelegate)
			{
				output.Append(", classHandle");
			}
			output.Append(");\n");
			AppendCppUnhandledExceptionHandling(
				cppMethodDefinitionsIndent + 3,
				output);
			AppendIndent(
				cppMethodDefinitionsIndent + 2,
				output);
			output.Append("}\n");
			AppendIndent(
				cppMethodDefinitionsIndent + 1,
				output);
			output.Append("}\n");
			if (typeIsDelegate)
			{
				AppendIndent(
					cppMethodDefinitionsIndent + 1,
					output);
				output.Append(
					"ClassHandle = other.ClassHandle;\n");
				AppendIndent(
					cppMethodDefinitionsIndent + 1,
					output);
				output.Append("other.ClassHandle = 0;\n");
			}
			AppendIndent(
				cppMethodDefinitionsIndent + 1,
				output);
			output.Append("Handle = other.Handle;\n");
			AppendIndent(
				cppMethodDefinitionsIndent + 1,
				output);
			output.Append("other.Handle = 0;\n");
			AppendIndent(
				cppMethodDefinitionsIndent + 1,
				output);
			output.Append("return *this;\n");
			AppendIndent(
				cppMethodDefinitionsIndent,
				output);
			output.Append("}\n");
			AppendIndent(
				cppMethodDefinitionsIndent,
				output);
			output.Append("\n");
		}

		static void AppendCppBaseTypeAssignmentOperatorNullptr(
			string numberedTypeName,
			Type[] typeParams,
			bool typeIsDelegate,
			string releaseFuncName,
			int cppMethodDefinitionsIndent,
			StringBuilder output)
		{
			AppendIndent(
				cppMethodDefinitionsIndent,
				output);
			AppendTypeNameWithoutGenericSuffix(
				numberedTypeName,
				output);
			AppendCppTypeParameters(
				typeParams,
				output);
			output.Append("& ");
			AppendTypeNameWithoutGenericSuffix(
				numberedTypeName,
				output);
			AppendCppTypeParameters(
				typeParams,
				output);
			output.Append(
				"::operator=(decltype(nullptr) other)\n");
			AppendIndent(
				cppMethodDefinitionsIndent,
				output);
			output.Append("{\n");
			AppendIndent(
				cppMethodDefinitionsIndent + 1,
				output);
			output.Append("if (Handle)\n");
			AppendIndent(
				cppMethodDefinitionsIndent + 1,
				output);
			output.Append("{\n");
			AppendIndent(
				cppMethodDefinitionsIndent + 2,
				output);
			output.Append("int32_t handle = Handle;\n");
			if (typeIsDelegate)
			{
				AppendIndent(
					cppMethodDefinitionsIndent + 2,
					output);
				output.Append("int32_t classHandle = ClassHandle;\n");
			}
			AppendIndent(
				cppMethodDefinitionsIndent + 2,
				output);
			output.Append("Handle = 0;\n");
			if (typeIsDelegate)
			{
				AppendIndent(
					cppMethodDefinitionsIndent + 2,
					output);
				output.Append("ClassHandle = 0;\n");
			}
			AppendIndent(
				cppMethodDefinitionsIndent + 2,
				output);
			output.Append(
				"if (Plugin::DereferenceManagedClassNoRelease(handle))\n");
			AppendIndent(
				cppMethodDefinitionsIndent + 2,
				output);
			output.Append("{\n");
			AppendIndent(
				cppMethodDefinitionsIndent + 3,
				output);
			output.Append("Plugin::");
			output.Append(releaseFuncName);
			output.Append("(handle");
			if (typeIsDelegate)
			{
				output.Append(", classHandle");
			}
			output.Append(");\n");
			AppendCppUnhandledExceptionHandling(
				cppMethodDefinitionsIndent + 3,
				output);
			AppendIndent(
				cppMethodDefinitionsIndent + 2,
				output);
			output.Append("}\n");
			AppendIndent(
				cppMethodDefinitionsIndent + 1,
				output);
			output.Append("}\n");
			if (typeIsDelegate)
			{
				AppendIndent(
					cppMethodDefinitionsIndent + 1,
					output);
				output.Append("ClassHandle = 0;\n");
			}
			AppendIndent(
				cppMethodDefinitionsIndent + 1,
				output);
			output.Append("Handle = 0;\n");
			AppendIndent(
				cppMethodDefinitionsIndent + 1,
				output);
			output.Append("return *this;\n");
			AppendIndent(
				cppMethodDefinitionsIndent,
				output);
			output.Append("}\n");
			AppendIndent(
				cppMethodDefinitionsIndent,
				output);
			output.Append("\n");
		}

		static void AppendCppBaseTypeAssignmentOperatorSameType(
			Type type,
			string numberedTypeName,
			Type[] typeParams,
			bool typeIsDelegate,
			int cppMethodDefinitionsIndent,
			StringBuilder output)
		{
			AppendIndent(
				cppMethodDefinitionsIndent,
				output);
			AppendTypeNameWithoutGenericSuffix(
				numberedTypeName,
				output);
			AppendCppTypeParameters(
				typeParams,
				output);
			output.Append("& ");
			AppendTypeNameWithoutGenericSuffix(
				numberedTypeName,
				output);
			AppendCppTypeParameters(
				typeParams,
				output);
			output.Append("::operator=(const ");
			AppendTypeNameWithoutGenericSuffix(
				numberedTypeName,
				output);
			AppendCppTypeParameters(
				typeParams,
				output);
			output.Append("& other)\n");
			AppendIndent(
				cppMethodDefinitionsIndent,
				output);
			output.Append("{\n");
			AppendSetHandle(
				numberedTypeName,
				type.Namespace,
				TypeKind.Class,
				typeParams,
				cppMethodDefinitionsIndent + 1,
				"this",
				"other.Handle",
				output);
			if (typeIsDelegate)
			{
				AppendIndent(
					cppMethodDefinitionsIndent + 1,
					output);
				output.Append(
					"ClassHandle = other.ClassHandle;\n");
			}
			AppendIndent(
				cppMethodDefinitionsIndent + 1,
				output);
			output.Append("return *this;\n");
			AppendIndent(
				cppMethodDefinitionsIndent,
				output);
			output.Append("}\n");
			AppendIndent(
				cppMethodDefinitionsIndent,
				output);
			output.Append("\n");
		}

		static void AppendCppBaseTypeDestructor(
			string typeName,
			string numberedTypeName,
			Type[] typeParams,
			bool typeIsDelegate,
			string releaseFuncName,
			int cppMethodDefinitionsIndent,
			StringBuilder output)
		{
			AppendIndent(
				cppMethodDefinitionsIndent,
				output);
			AppendTypeNameWithoutGenericSuffix(
				numberedTypeName,
				output);
			AppendCppTypeParameters(
				typeParams,
				output);
			output.Append("::~");
			AppendTypeNameWithoutGenericSuffix(
				numberedTypeName,
				output);
			output.Append("()\n");
			AppendIndent(
				cppMethodDefinitionsIndent,
				output);
			output.Append("{\n");
			AppendIndent(
				cppMethodDefinitionsIndent + 1,
				output);
			output.Append("Plugin::Remove");
			output.Append(typeName);
			output.Append("(CppHandle);\n");
			AppendIndent(
				cppMethodDefinitionsIndent + 1,
				output);
			output.Append("CppHandle = 0;\n");
			AppendIndent(
				cppMethodDefinitionsIndent + 1,
				output);
			output.Append("if (Handle)\n");
			AppendIndent(
				cppMethodDefinitionsIndent + 1,
				output);
			output.Append("{\n");
			AppendIndent(
				cppMethodDefinitionsIndent + 2,
				output);
			output.Append("int32_t handle = Handle;\n");
			if (typeIsDelegate)
			{
				AppendIndent(
					cppMethodDefinitionsIndent + 2,
					output);
				output.Append("int32_t classHandle = ClassHandle;\n");
			}
			AppendIndent(
				cppMethodDefinitionsIndent + 2,
				output);
			output.Append("Handle = 0;\n");
			if (typeIsDelegate)
			{
				AppendIndent(
					cppMethodDefinitionsIndent + 2,
					output);
				output.Append("ClassHandle = 0;\n");
			}
			AppendIndent(
				cppMethodDefinitionsIndent + 2,
				output);
			output.Append(
				"if (Plugin::DereferenceManagedClassNoRelease(handle))\n");
			AppendIndent(
				cppMethodDefinitionsIndent + 2,
				output);
			output.Append("{\n");
			AppendIndent(
				cppMethodDefinitionsIndent + 3,
				output);
			output.Append("Plugin::");
			output.Append(releaseFuncName);
			output.Append("(handle");
			if (typeIsDelegate)
			{
				output.Append(", classHandle");
			}
			output.Append(");\n");
			AppendCppUnhandledExceptionHandling(
				cppMethodDefinitionsIndent + 3,
				output);
			AppendIndent(
				cppMethodDefinitionsIndent + 2,
				output);
			output.Append("}\n");
			AppendIndent(
				cppMethodDefinitionsIndent + 1,
				output);
			output.Append("}\n");
			AppendIndent(
				cppMethodDefinitionsIndent,
				output);
			output.Append("}\n");
			AppendIndent(
				cppMethodDefinitionsIndent,
				output);
			output.Append("\n");
		}

		static void AppendCppBaseTypeHandleConstructor(
			string typeName,
			string numberedTypeName,
			Type[] typeParams,
			bool typeIsDelegate,
			int cppMethodDefinitionsIndent,
			StringBuilder output)
		{
			AppendIndent(
				cppMethodDefinitionsIndent,
				output);
			AppendTypeNameWithoutGenericSuffix(
				numberedTypeName,
				output);
			AppendCppTypeParameters(
				typeParams,
				output);
			output.Append("::");
			AppendTypeNameWithoutGenericSuffix(
				numberedTypeName,
				output);
			output.Append(
				"(Plugin::InternalUse iu, int32_t handle)\n");
			AppendIndent(
				cppMethodDefinitionsIndent,
				output);
			output.Append(
				"\t: System::Object(iu, handle)\n");
			AppendIndent(
				cppMethodDefinitionsIndent,
				output);
			output.Append("{\n");
			AppendIndent(
				cppMethodDefinitionsIndent + 1,
				output);
			output.Append("CppHandle = Plugin::Store");
			output.Append(typeName);
			output.Append("(this);\n");
			AppendIndent(
				cppMethodDefinitionsIndent + 1,
				output);
			output.Append("if (Handle)\n");
			AppendIndent(
				cppMethodDefinitionsIndent + 1,
				output);
			output.Append("{\n");
			AppendIndent(
				cppMethodDefinitionsIndent + 2,
				output);
			output.Append(
				"Plugin::ReferenceManagedClass(Handle);\n");
			AppendIndent(
				cppMethodDefinitionsIndent + 1,
				output);
			output.Append("}\n");
			if (typeIsDelegate)
			{
				AppendIndent(
					cppMethodDefinitionsIndent + 1,
					output);
				output.Append(
					"ClassHandle = 0;\n");
			}
			AppendIndent(
				cppMethodDefinitionsIndent,
				output);
			output.Append("}\n");
			AppendIndent(
				cppMethodDefinitionsIndent,
				output);
			output.Append("\n");
		}

		static void AppendCppBaseTypeMoveConstructor(
			string numberedTypeName,
			Type[] typeParams,
			bool typeIsDelegate,
			int cppMethodDefinitionsIndent,
			StringBuilder output)
		{
			AppendIndent(
				cppMethodDefinitionsIndent,
				output);
			AppendTypeNameWithoutGenericSuffix(
				numberedTypeName,
				output);
			AppendCppTypeParameters(
				typeParams,
				output);
			output.Append("::");
			AppendTypeNameWithoutGenericSuffix(
				numberedTypeName,
				output);
			output.Append("(");
			AppendTypeNameWithoutGenericSuffix(
				numberedTypeName,
				output);
			AppendCppTypeParameters(
				typeParams,
				output);
			output.Append("&& other)\n");
			AppendIndent(
				cppMethodDefinitionsIndent,
				output);
			output.Append(
				"\t: System::Object(Plugin::InternalUse::Only, other.Handle)\n");
			AppendIndent(
				cppMethodDefinitionsIndent,
				output);
			output.Append("{\n");
			AppendIndent(
				cppMethodDefinitionsIndent + 1,
				output);
			output.Append(
				"CppHandle = other.CppHandle;\n");
			if (typeIsDelegate)
			{
				AppendIndent(
					cppMethodDefinitionsIndent + 1,
					output);
				output.Append(
					"ClassHandle = other.ClassHandle;\n");
			}
			AppendIndent(
				cppMethodDefinitionsIndent + 1,
				output);
			output.Append("other.Handle = 0;\n");
			AppendIndent(
				cppMethodDefinitionsIndent + 1,
				output);
			output.Append("other.CppHandle = 0;\n");
			if (typeIsDelegate)
			{
				AppendIndent(
					cppMethodDefinitionsIndent + 1,
					output);
				output.Append("other.ClassHandle = 0;\n");
			}
			AppendIndent(
				cppMethodDefinitionsIndent,
				output);
			output.Append("}\n");
			AppendIndent(
				cppMethodDefinitionsIndent,
				output);
			output.Append("\n");
		}

		static void AppendCppBaseTypeCopyConstructor(
			string typeName,
			string numberedTypeName,
			Type[] typeParams,
			bool typeIsDelegate,
			int cppMethodDefinitionsIndent,
			StringBuilder output)
		{
			AppendIndent(
				cppMethodDefinitionsIndent,
				output);
			AppendTypeNameWithoutGenericSuffix(
				numberedTypeName,
				output);
			AppendCppTypeParameters(
				typeParams,
				output);
			output.Append("::");
			AppendTypeNameWithoutGenericSuffix(
				numberedTypeName,
				output);
			output.Append("(const ");
			AppendTypeNameWithoutGenericSuffix(
				numberedTypeName,
				output);
			AppendCppTypeParameters(
				typeParams,
				output);
			output.Append("& other)\n");
			AppendIndent(
				cppMethodDefinitionsIndent,
				output);
			output.Append(
				"\t: System::Object(Plugin::InternalUse::Only, other.Handle)\n");
			AppendIndent(
				cppMethodDefinitionsIndent,
				output);
			output.Append("{\n");
			AppendIndent(
				cppMethodDefinitionsIndent + 1,
				output);
			output.Append("CppHandle = Plugin::Store");
			output.Append(typeName);
			output.Append("(this);\n");
			AppendIndent(
				cppMethodDefinitionsIndent + 1,
				output);
			output.Append("if (Handle)\n");
			AppendIndent(
				cppMethodDefinitionsIndent + 1,
				output);
			output.Append("{\n");
			AppendIndent(
				cppMethodDefinitionsIndent + 2,
				output);
			output.Append(
				"Plugin::ReferenceManagedClass(Handle);\n");
			AppendIndent(
				cppMethodDefinitionsIndent + 1,
				output);
			output.Append("}\n");
			if (typeIsDelegate)
			{
				AppendIndent(
					cppMethodDefinitionsIndent + 1,
					output);
				output.Append(
					"ClassHandle = other.ClassHandle;\n");
			}
			AppendIndent(
				cppMethodDefinitionsIndent,
				output);
			output.Append("}\n");
			AppendIndent(
				cppMethodDefinitionsIndent,
				output);
			output.Append("\n");
		}

		static void AppendCppBaseTypeNullptrConstructor(
			string typeName,
			string numberedTypeName,
			Type[] typeParams,
			bool typeIsDelegate,
			int cppMethodDefinitionsIndent,
			StringBuilder output)
		{
			AppendIndent(
				cppMethodDefinitionsIndent,
				output);
			AppendTypeNameWithoutGenericSuffix(
				numberedTypeName,
				output);
			AppendCppTypeParameters(
				typeParams,
				output);
			output.Append("::");
			AppendTypeNameWithoutGenericSuffix(
				numberedTypeName,
				output);
			output.Append("(decltype(nullptr) n)\n");
			AppendIndent(
				cppMethodDefinitionsIndent,
				output);
			output.Append(
				"\t: System::Object(Plugin::InternalUse::Only, 0)\n");
			AppendIndent(
				cppMethodDefinitionsIndent,
				output);
			output.Append("{\n");
			AppendIndent(
				cppMethodDefinitionsIndent + 1,
				output);
			output.Append("CppHandle = Plugin::Store");
			output.Append(typeName);
			output.Append("(this);\n");
			if (typeIsDelegate)
			{
				AppendIndent(
					cppMethodDefinitionsIndent + 1,
					output);
				output.Append("ClassHandle = 0;\n");
			}
			AppendIndent(
				cppMethodDefinitionsIndent,
				output);
			output.Append("}\n");
			AppendIndent(
				cppMethodDefinitionsIndent,
				output);
			output.Append("\n");
		}

		static void AppendCppBaseTypeDefaultConstructor(
			string typeName,
			string numberedTypeName,
			Type[] typeParams,
			bool typeIsDelegate,
			string constructorFuncName,
			int cppMethodDefinitionsIndent,
			StringBuilder output)
		{
			AppendCppMethodDefinitionBegin(
				numberedTypeName,
				null,
				numberedTypeName,
				typeParams,
				null,
				new ParameterInfo[0],
				cppMethodDefinitionsIndent,
				output);
			AppendIndent(
				cppMethodDefinitionsIndent + 1,
				output);
			output.Append(" : System::Object(nullptr)\n");
			AppendIndent(
				cppMethodDefinitionsIndent,
				output);
			output.Append("{\n");
			AppendIndent(
				cppMethodDefinitionsIndent + 1,
				output);
			output.Append("CppHandle = Plugin::Store");
			output.Append(typeName);
			output.Append("(this);\n");
			AppendIndent(
				cppMethodDefinitionsIndent + 1,
				output);
			output.Append("Plugin::");
			output.Append(constructorFuncName);
			output.Append("(CppHandle, &Handle");
			if (typeIsDelegate)
			{
				output.Append(", &ClassHandle");
			}
			output.Append(");\n");
			AppendIndent(
				cppMethodDefinitionsIndent + 1,
				output);
			output.Append("if (Handle)\n");
			AppendIndent(
				cppMethodDefinitionsIndent + 1,
				output);
			output.Append("{\n");
			AppendIndent(
				cppMethodDefinitionsIndent + 2,
				output);
			output.Append(
				"Plugin::ReferenceManagedClass(Handle);\n");
			AppendIndent(
				cppMethodDefinitionsIndent + 1,
				output);
			output.Append("}\n");
			AppendIndent(
				cppMethodDefinitionsIndent + 1,
				output);
			output.Append("else\n");
			AppendIndent(
				cppMethodDefinitionsIndent + 1,
				output);
			output.Append("{\n");
			AppendIndent(
				cppMethodDefinitionsIndent + 2,
				output);
			output.Append("Plugin::Remove");
			output.Append(typeName);
			output.Append("(CppHandle);\n");
			if (typeIsDelegate)
			{
				AppendIndent(
					cppMethodDefinitionsIndent + 2,
					output);
				output.Append("ClassHandle = 0;\n");
			}
			AppendIndent(
				cppMethodDefinitionsIndent + 2,
				output);
			output.Append("CppHandle = 0;\n");
			AppendIndent(
				cppMethodDefinitionsIndent + 1,
				output);
			output.Append("}\n");
			AppendCppUnhandledExceptionHandling(
				cppMethodDefinitionsIndent + 1,
				output);
			AppendIndent(
				cppMethodDefinitionsIndent,
				output);
			output.Append("}\n");
			AppendIndent(
				cppMethodDefinitionsIndent,
				output);
			output.Append('\n');
		}

		static void AppendCppFreeListInit(
			Type type,
			int? maxSimultaneous,
			string typeName,
			StringBuilder output)
		{
			output.Append('\t');
			output.Append(typeName);
			output.Append("FreeListSize = ");
			if (maxSimultaneous.HasValue)
			{
				output.Append(maxSimultaneous);
			}
			else
			{
				output.Append("maxManagedObjects");
			}
			output.Append(";\n");
			output.Append("\t");
			output.Append(typeName);
			output.Append("FreeList = new ");
			AppendCppTypeName(
				type,
				output);
			output.Append("*[");
			output.Append(typeName);
			output.Append("FreeListSize];\n");
			output.Append("\tfor (int32_t i = 0, end = ");
			output.Append(typeName);
			output.Append("FreeListSize - 1; i < end; ++i)\n");
			output.Append("\t{\n");
			output.Append("\t	");
			output.Append(typeName);
			output.Append("FreeList[i] = (");
			AppendCppTypeName(
				type,
				output);
			output.Append("*)(");
			output.Append(typeName);
			output.Append("FreeList + i + 1);\n");
			output.Append("\t}\n");
			output.Append('\t');
			output.Append(typeName);
			output.Append("FreeList[");
			output.Append(typeName);
			output.Append("FreeListSize - 1] = nullptr;\n");
			output.Append("\tNextFree");
			output.Append(typeName);
			output.Append(" = ");
			output.Append(typeName);
			output.Append("FreeList + 1;\n");
		}

		static void AppendCppFreeListStateAndFunctions(
			Type type,
			string typeName,
			StringBuilder output)
		{
			output.Append("\tint32_t ");
			output.Append(typeName);
			output.Append("FreeListSize;\n");
			output.Append('\t');
			AppendCppTypeName(
				type,
				output);
			output.Append("** ");
			output.Append(typeName);
			output.Append("FreeList;\n");
			output.Append('\t');
			AppendCppTypeName(
				type,
				output);
			output.Append("** NextFree");
			output.Append(typeName);
			output.Append(";\n");
			output.Append("\t\n");
			output.Append("\tint32_t Store");
			output.Append(typeName);
			output.Append('(');
			AppendCppTypeName(
				type,
				output);
			output.Append("* del)\n");
			output.Append("\t{\n");
			output.Append("\t\tassert(NextFree");
			output.Append(typeName);
			output.Append(" != nullptr);\n");
			output.Append("\t\t");
			AppendCppTypeName(
				type,
				output);
			output.Append("** pNext = NextFree");
			output.Append(typeName);
			output.Append(";\n");
			output.Append("\t\tNextFree");
			output.Append(typeName);
			output.Append(" = (");
			AppendCppTypeName(
				type,
				output);
			output.Append("**)*pNext;\n");
			output.Append("\t\t*pNext = del;\n");
			output.Append("\t\treturn (int32_t)(pNext - ");
			output.Append(typeName);
			output.Append("FreeList);\n");
			output.Append("\t}\n");
			output.Append("\t\n");
			output.Append('\t');
			AppendCppTypeName(
				type,
				output);
			output.Append("* Get");
			output.Append(typeName);
			output.Append("(int32_t handle)\n");
			output.Append("\t{\n");
			output.Append(
				"\t\tassert(handle >= 0 && handle < ");
			output.Append(typeName);
			output.Append("FreeListSize);\n");
			output.Append("\t\treturn ");
			output.Append(typeName);
			output.Append("FreeList[handle];\n");
			output.Append("\t}\n");
			output.Append("\t\n");
			output.Append("\tvoid Remove");
			output.Append(typeName);
			output.Append("(int32_t handle)\n");
			output.Append("\t{\n");
			output.Append("\t\t");
			AppendCppTypeName(
				type,
				output);
			output.Append("** pRelease = ");
			output.Append(typeName);
			output.Append("FreeList + handle;\n");
			output.Append("\t\t*pRelease = (");
			AppendCppTypeName(
				type,
				output);
			output.Append("*)NextFree");
			output.Append(typeName);
			output.Append(";\n");
			output.Append("\t\tNextFree");
			output.Append(typeName);
			output.Append(" = pRelease;\n");
			output.Append("\t}\n");
		}

		static void AppendCsharpDelegate(
			bool isStatic,
			string typeName,
			string typeNamespace,
			Type[] typeParams,
			string funcName,
			ParameterInfo[] parameters,
			Type returnType,
			TypeKind returnTypeKind,
			StringBuilder output)
		{
			output.Append("\t\tpublic delegate ");
			if (returnType == typeof(void))
			{
				output.Append("void");
			}
			else
			{
				switch (returnTypeKind)
				{
					case TypeKind.Class:
					case TypeKind.ManagedStruct:
						output.Append("int");
						break;
					default:
						AppendCsharpTypeName(
							returnType,
							output);
						break;
				}
			}
			output.Append(' ');
			AppendCsharpDelegateName(
				typeName,
				typeNamespace,
				typeParams,
				funcName,
				output);
			output.Append("Delegate(");
			if (!isStatic)
			{
				output.Append("int thisHandle");
				if (parameters.Length > 0)
				{
					output.Append(", ");
				}
			}
			for (int i = 0; i < parameters.Length; ++i)
			{
				ParameterInfo param = parameters[i];
				switch (param.Kind)
				{
					case TypeKind.FullStruct:
					case TypeKind.Primitive:
					case TypeKind.Enum:
						AppendCsharpTypeName(
							param.ParameterType,
							output);
						output.Append(" param");
						output.Append(i);
						break;
					default:
						output.Append("int param");
						output.Append(i);
						break;
				}
				if (i != parameters.Length-1)
				{
					output.Append(", ");
				}
			}
			output.Append(");\n");
			output.Append("\t\tpublic static ");
			AppendCsharpDelegateName(
				typeName,
				typeNamespace,
				typeParams,
				funcName,
				output);
			output.Append("Delegate ");
			AppendCsharpDelegateName(
				typeName,
				typeNamespace,
				typeParams,
				funcName,
				output);
			output.Append(";\n\t\t\n");
		}
		
		static void AppendCsharpDelegateName(
			string typeName,
			string typeNamespace,
			Type[] typeParams,
			string funcName,
			StringBuilder output)
		{
			AppendNamespace(
				typeNamespace,
				string.Empty,
				output);
			AppendTypeNameWithoutSuffixes(
				typeName,
				output);
			AppendTypeNames(
				typeParams,
				output);
			output.Append(funcName);
		}
		
		static void AppendCsharpGetDelegateCall(
			string typeName,
			string typeNamespace,
			Type[] typeParams,
			string funcName,
			StringBuilder output)
		{
			output.Append("\t\t\t");
			AppendCsharpDelegateName(
				typeName,
				typeNamespace,
				typeParams,
				funcName,
				output);
			output.Append(" = GetDelegate<");
			AppendCsharpDelegateName(
				typeName,
				typeNamespace,
				typeParams,
				funcName,
				output);
			output.Append("Delegate>(libraryHandle, \"");
			AppendCsharpDelegateName(
				typeName,
				typeNamespace,
				typeParams,
				funcName,
				output);
			output.Append("\");\n");
		}
		
		static void AppendCsharpImport(
			string typeName,
			string typeNamespace,
			Type[] typeParams,
			string funcName,
			ParameterInfo[] parameters,
			StringBuilder output
		)
		{
			output.Append("\t\t[DllImport(Constants.PluginName)]\n");
			output.Append("\t\tpublic static extern void ");
			AppendCsharpDelegateName(
				typeName,
				typeNamespace,
				typeParams,
				funcName,
				output);
			output.Append("(int thisHandle");
			if (parameters.Length > 0)
			{
				output.Append(", ");
			}
			for (int i = 0; i < parameters.Length; ++i)
			{
				ParameterInfo param = parameters[i];
				if (param.Kind == TypeKind.FullStruct)
				{
					AppendCsharpTypeName(
						param.ParameterType,
						output);
					output.Append(" param");
					output.Append(i);
				}
				else
				{
					output.Append("int param");
					output.Append(i);
				}
				if (i != parameters.Length-1)
				{
					output.Append(", ");
				}
			}
			output.Append(");\n\t\t\n");
		}
		
		static void AppendExceptions(
			JsonDocument doc,
			Assembly[] assemblies,
			StringBuilders builders)
		{
			// Gather all specific types of exceptions
			Dictionary<string, Type> exceptionTypes = new Dictionary<string, Type>();
			if (doc.Types != null)
			{
				foreach (JsonType jsonType in doc.Types)
				{
					if (jsonType.Methods != null)
					{
						foreach (JsonMethod jsonMethod in jsonType.Methods)
						{
							if (jsonMethod.Exceptions != null)
							{
								AddUniqueTypes(
									jsonMethod.Exceptions,
									exceptionTypes,
									assemblies);
							}
						}
					}
					if (jsonType.Constructors != null)
					{
						foreach (JsonConstructor jsonCtor in jsonType.Constructors)
						{
							if (jsonCtor.Exceptions != null)
							{
								AddUniqueTypes(
									jsonCtor.Exceptions,
									exceptionTypes,
									assemblies);
							}
						}
					}
					if (jsonType.Properties != null)
					{
						foreach (JsonProperty jsonProperty in jsonType.Properties)
						{
							JsonPropertyGet jsonPropertyGet = jsonProperty.Get;
							if (jsonPropertyGet != null
								&& jsonPropertyGet.Exceptions != null)
							{
								AddUniqueTypes(
									jsonPropertyGet.Exceptions,
									exceptionTypes,
									assemblies);
							}
							JsonPropertySet jsonPropertySet = jsonProperty.Set;
							if (jsonPropertySet != null
								&& jsonPropertySet.Exceptions != null)
							{
								AddUniqueTypes(
									jsonPropertySet.Exceptions,
									exceptionTypes,
									assemblies);
							}
						}
					}
				}
			}
			
			foreach (Type exceptionType in exceptionTypes.Values)
			{
				// Build function name
				builders.TempStrBuilder.Length = 0;
				AppendCsharpSetCsharpExceptionFunctionName(
					exceptionType,
					builders.TempStrBuilder);
				string funcName = builders.TempStrBuilder.ToString();
				
				// C++ thrower type
				int throwerIndent = AppendNamespaceBeginning(
					exceptionType.Namespace,
					builders.CppMethodDefinitions);
				AppendIndent(
					throwerIndent,
					builders.CppMethodDefinitions);
				builders.CppMethodDefinitions.Append("struct ");
				builders.CppMethodDefinitions.Append(exceptionType.Name);
				builders.CppMethodDefinitions.Append("Thrower : ");
				AppendCppTypeName(
					exceptionType,
					builders.CppMethodDefinitions);
				builders.CppMethodDefinitions.Append('\n');
				AppendIndent(
					throwerIndent,
					builders.CppMethodDefinitions);
				builders.CppMethodDefinitions.Append("{\n");
				AppendIndent(
					throwerIndent + 1,
					builders.CppMethodDefinitions);
				builders.CppMethodDefinitions.Append(exceptionType.Name);
				builders.CppMethodDefinitions.Append("Thrower(int32_t handle)\n");
				AppendIndent(
					throwerIndent + 2,
					builders.CppMethodDefinitions);
				builders.CppMethodDefinitions.Append(": ");
				AppendCppTypeName(
					exceptionType,
					builders.CppMethodDefinitions);
				builders.CppMethodDefinitions.Append("(Plugin::InternalUse::Only, handle)\n");
				AppendIndent(
					throwerIndent + 1,
					builders.CppMethodDefinitions);
				builders.CppMethodDefinitions.Append("{\n");
				AppendIndent(
					throwerIndent + 1,
					builders.CppMethodDefinitions);
				builders.CppMethodDefinitions.Append("}\n");
				AppendIndent(
					throwerIndent,
					builders.CppMethodDefinitions);
				builders.CppMethodDefinitions.Append("\n");
				AppendIndent(
					throwerIndent + 1,
					builders.CppMethodDefinitions);
				builders.CppMethodDefinitions.Append("virtual void ThrowReferenceToThis()\n");
				AppendIndent(
					throwerIndent + 1,
					builders.CppMethodDefinitions);
				builders.CppMethodDefinitions.Append("{\n");
				AppendIndent(
					throwerIndent + 2,
					builders.CppMethodDefinitions);
				builders.CppMethodDefinitions.Append("throw *this;\n");
				AppendIndent(
					throwerIndent + 1,
					builders.CppMethodDefinitions);
				builders.CppMethodDefinitions.Append("}\n");
				AppendIndent(
					throwerIndent,
					builders.CppMethodDefinitions);
				builders.CppMethodDefinitions.Append("};\n");
				AppendNamespaceEnding(
					throwerIndent,
					builders.CppMethodDefinitions);
				builders.CppMethodDefinitions.Append('\n');
				
				// C++ function
				builders.CppMethodDefinitions.Append("DLLEXPORT void ");
				builders.CppMethodDefinitions.Append(funcName);
				builders.CppMethodDefinitions.Append("(int32_t handle)\n");
				builders.CppMethodDefinitions.Append("{\n");
				builders.CppMethodDefinitions.Append("\tdelete Plugin::unhandledCsharpException;\n");
				builders.CppMethodDefinitions.Append("\tPlugin::unhandledCsharpException = new ");
				AppendCppTypeName(
					exceptionType,
					builders.CppMethodDefinitions);
				builders.CppMethodDefinitions.Append("Thrower(handle);\n");
				builders.CppMethodDefinitions.Append("}\n\n");
				
				// Build parameters
				ParameterInfo[] parameters = ConvertParameters(
					new[]{ typeof(int) });
				
				// C# imports
				AppendCsharpImport(
					string.Empty,
					string.Empty,
					null,
					funcName,
					parameters,
					builders.CsharpImports);
				
				// C# delegate
				AppendCsharpDelegate(
					true,
					string.Empty,
					string.Empty,
					null,
					funcName,
					parameters,
					typeof(void),
					TypeKind.None,
					builders.CsharpDelegates
				);
				
				// C# GetDelegate call
				AppendCsharpGetDelegateCall(
					string.Empty,
					string.Empty,
					null,
					funcName,
					builders.CsharpGetDelegateCalls);
			}
		}
		
		static void AddUniqueTypes(
			string[] typeNames,
			Dictionary<string, Type> types,
			Assembly[] assemblies)
		{
			foreach (string typeName in typeNames)
			{
				if (!types.ContainsKey(typeName))
				{
					Type type = GetType(
						typeName,
						assemblies);
					types.Add(
						typeName,
						type);
				}
			}
		}
		
		static void AppendGetter(
			string fieldName,
			string syntaxType,
			ParameterInfo[] parameters,
			bool enclosingTypeIsStatic,
			TypeKind enclosingTypeKind,
			bool methodIsStatic,
			bool isReadOnly,
			Type enclosingType,
			Type[] enclosingTypeParams,
			Type fieldType,
			TypeKind fieldTypeKind,
			int indent,
			Type[] exceptionTypes,
			StringBuilders builders)
		{
			// Build uppercase field name
			builders.TempStrBuilder.Length = 0;
			builders.TempStrBuilder.Append(char.ToUpper(fieldName[0]));
			builders.TempStrBuilder.Append(
				fieldName,
				1,
				fieldName.Length-1);
			string fieldNameUpper = builders.TempStrBuilder.ToString();
			
			// Build uppercase function name
			builders.TempStrBuilder.Length = 0;
			AppendNamespace(
				enclosingType.Namespace,
				string.Empty,
				builders.TempStrBuilder);
			AppendTypeNameWithoutGenericSuffix(
				enclosingType.Name,
				builders.TempStrBuilder);
			AppendTypeNames(
				enclosingTypeParams,
				builders.TempStrBuilder);
			builders.TempStrBuilder.Append(syntaxType);
			builders.TempStrBuilder.Append("Get");
			builders.TempStrBuilder.Append(fieldNameUpper);
			string funcName = builders.TempStrBuilder.ToString();
			
			// Build lowercase function name
			builders.TempStrBuilder[0] = char.ToLower(
				builders.TempStrBuilder[0]);
			string funcNameLower = builders.TempStrBuilder.ToString();
			
			// Build method name
			builders.TempStrBuilder.Length = 0;
			builders.TempStrBuilder.Append("Get");
			builders.TempStrBuilder.Append(fieldNameUpper);
			string methodName = builders.TempStrBuilder.ToString();
			
			// C# init param declaration
			AppendCsharpInitParam(
				funcNameLower,
				builders.CsharpInitParams);

			// C# delegate type
			AppendCsharpDelegateType(
				funcName,
				methodIsStatic,
				enclosingType,
				enclosingTypeKind,
				fieldType,
				parameters,
				builders.CsharpDelegateTypes);

			// C# init call param
			AppendCsharpInitCallArg(
				funcName,
				builders.CsharpInitCall);

			// C# function
			AppendCsharpFunctionBeginning(
				enclosingType,
				funcName,
				methodIsStatic,
				enclosingTypeKind,
				fieldType,
				parameters,
				builders.CsharpFunctions);
			AppendCsharpFunctionCallSubject(
				enclosingType,
				methodIsStatic,
				builders.CsharpFunctions);
			if (parameters.Length > 0)
			{
				builders.CsharpFunctions.Append('[');
				for (int i = 0; i < parameters.Length; ++i)
				{
					builders.CsharpFunctions.Append(parameters[0].Name);
					if (i != parameters.Length-1)
					{
						builders.CsharpFunctions.Append(", ");
					}
				}
				builders.CsharpFunctions.Append("]");
			}
			else
			{
				builders.CsharpFunctions.Append('.');
				builders.CsharpFunctions.Append(fieldName);
			}
			builders.CsharpFunctions.Append(';');
			if (!isReadOnly
				&& enclosingTypeKind == TypeKind.ManagedStruct)
			{
				AppendStructStoreReplace(
					enclosingType,
					"thisHandle",
					"thiz",
					builders.CsharpFunctions);
			}
			AppendCsharpFunctionReturn(
				parameters,
				fieldType,
				fieldTypeKind,
				exceptionTypes,
				false,
				builders.CsharpFunctions);

			// C++ function pointer
			AppendCppFunctionPointerDefinition(
				funcName,
				methodIsStatic,
				enclosingType.Name,
				enclosingType.Namespace,
				enclosingTypeKind,
				parameters,
				fieldType,
				builders.CppFunctionPointers);

			// C++ method declaration
			AppendIndent(indent + 1, builders.CppTypeDefinitions);
			AppendCppMethodDeclaration(
				methodName,
				enclosingTypeIsStatic,
				false,
				methodIsStatic,
				fieldType,
				null,
				parameters,
				builders.CppTypeDefinitions);
			
			// C++ method definition
			AppendCppMethodDefinitionBegin(
				enclosingType.Name,
				fieldType,
				methodName,
				enclosingTypeParams,
				null,
				parameters,
				indent,
				builders.CppMethodDefinitions);
			AppendIndent(indent, builders.CppMethodDefinitions);
			builders.CppMethodDefinitions.Append("{\n");
			AppendCppPluginFunctionCall(
				methodIsStatic,
				enclosingType.Name,
				enclosingType.Namespace,
				enclosingTypeKind,
				enclosingTypeParams,
				fieldType,
				funcName,
				parameters,
				indent + 1,
				builders.CppMethodDefinitions);
			AppendCppMethodReturn(
				fieldType,
				fieldTypeKind,
				indent + 1,
				builders.CppMethodDefinitions);
			AppendIndent(indent, builders.CppMethodDefinitions);
			builders.CppMethodDefinitions.Append("}\n");
			AppendIndent(indent, builders.CppMethodDefinitions);
			builders.CppMethodDefinitions.Append('\n');

			// C++ init params
			AppendCppInitParam(
				funcNameLower,
				methodIsStatic,
				enclosingType.Name,
				enclosingType.Namespace,
				enclosingTypeKind,
				parameters,
				fieldType,
				builders.CppInitParams);
			
			// C++ init body
			AppendCppInitBody(
				funcName,
				funcNameLower,
				builders.CppInitBody);
		}
		
		static void AppendSetter(
			string fieldName,
			string syntaxType,
			ParameterInfo[] parameters,
			bool enclosingTypeIsStatic,
			TypeKind enclosingTypeKind,
			bool methodIsStatic,
			bool isReadOnly,
			Type enclosingType,
			Type[] enclosingTypeParams,
			int indent,
			Type[] exceptionTypes,
			StringBuilders builders)
		{
			// Build uppercased field name
			builders.TempStrBuilder.Length = 0;
			builders.TempStrBuilder.Append(char.ToUpper(fieldName[0]));
			builders.TempStrBuilder.Append(
				fieldName,
				1,
				fieldName.Length-1);
			string fieldNameUpper = builders.TempStrBuilder.ToString();
			
			// Build uppercase function name
			builders.TempStrBuilder.Length = 0;
			AppendNamespace(
				enclosingType.Namespace,
				string.Empty,
				builders.TempStrBuilder);
			AppendTypeNameWithoutGenericSuffix(
				enclosingType.Name,
				builders.TempStrBuilder);
			AppendTypeNames(
				enclosingTypeParams,
				builders.TempStrBuilder);
			builders.TempStrBuilder.Append(syntaxType);
			builders.TempStrBuilder.Append("Set");
			builders.TempStrBuilder.Append(fieldNameUpper);
			string funcName = builders.TempStrBuilder.ToString();
			
			// Build lowercase function name
			builders.TempStrBuilder[0] = char.ToLower(
				builders.TempStrBuilder[0]);
			string funcNameLower = builders.TempStrBuilder.ToString();
			
			// Build method name
			builders.TempStrBuilder.Length = 0;
			builders.TempStrBuilder.Append("Set");
			builders.TempStrBuilder.Append(fieldNameUpper);
			string methodName = builders.TempStrBuilder.ToString();
			
			// C# init param declaration
			AppendCsharpInitParam(
				funcNameLower,
				builders.CsharpInitParams);
			
			// C# delegate type
			AppendCsharpDelegateType(
				funcName,
				methodIsStatic,
				enclosingType,
				enclosingTypeKind,
				typeof(void),
				parameters,
				builders.CsharpDelegateTypes);
			
			// C# init call param
			AppendCsharpInitCallArg(
				funcName,
				builders.CsharpInitCall);
			
			// C# function
			AppendCsharpFunctionBeginning(
				enclosingType,
				funcName,
				methodIsStatic,
				enclosingTypeKind,
				typeof(void),
				parameters,
				builders.CsharpFunctions);
			AppendCsharpFunctionCallSubject(
				enclosingType,
				methodIsStatic,
				builders.CsharpFunctions);
			if (parameters.Length > 1)
			{
				builders.CsharpFunctions.Append('[');
				for (int i = 0, end = parameters.Length-1; i < end; ++i)
				{
					builders.CsharpFunctions.Append(parameters[i].Name);
					if (i != end-1)
					{
						builders.CsharpFunctions.Append(", ");
					}
				}
				builders.CsharpFunctions.Append("] = ");
				builders.CsharpFunctions.Append(parameters[1].Name);
			}
			else
			{
				builders.CsharpFunctions.Append('.');
				builders.CsharpFunctions.Append(fieldName);
				builders.CsharpFunctions.Append(" = ");
				builders.CsharpFunctions.Append("value");
			}
			builders.CsharpFunctions.Append(';');
			if (!isReadOnly
				&& enclosingTypeKind == TypeKind.ManagedStruct)
			{
				AppendStructStoreReplace(
					enclosingType,
					"thisHandle",
					"thiz",
					builders.CsharpFunctions);
			}
			AppendCsharpFunctionReturn(
				parameters,
				typeof(void),
				TypeKind.None,
				exceptionTypes,
				false,
				builders.CsharpFunctions);
			
			// C++ function pointer
			AppendCppFunctionPointerDefinition(
				funcName,
				methodIsStatic,
				enclosingType.Name,
				enclosingType.Namespace,
				enclosingTypeKind,
				parameters,
				typeof(void),
				builders.CppFunctionPointers);
			
			// C++ method declaration
			AppendIndent(indent + 1, builders.CppTypeDefinitions);
			AppendCppMethodDeclaration(
				methodName,
				enclosingTypeIsStatic,
				false,
				methodIsStatic,
				typeof(void),
				null,
				parameters,
				builders.CppTypeDefinitions);
			
			// C++ method definition
			AppendCppMethodDefinitionBegin(
				enclosingType.Name,
				typeof(void),
				methodName,
				enclosingTypeParams,
				null,
				parameters,
				indent,
				builders.CppMethodDefinitions);
			AppendIndent(indent, builders.CppMethodDefinitions);
			builders.CppMethodDefinitions.Append("{\n");
			AppendCppPluginFunctionCall(
				methodIsStatic,
				enclosingType.Name,
				enclosingType.Namespace,
				enclosingTypeKind,
				enclosingTypeParams,
				null,
				funcName,
				parameters,
				indent + 1,
				builders.CppMethodDefinitions);
			AppendIndent(indent, builders.CppMethodDefinitions);
			builders.CppMethodDefinitions.Append("}\n");
			AppendIndent(indent, builders.CppMethodDefinitions);
			builders.CppMethodDefinitions.Append('\n');
			
			// C++ init params
			AppendCppInitParam(
				funcNameLower,
				methodIsStatic,
				enclosingType.Name,
				enclosingType.Namespace,
				enclosingTypeKind,
				parameters,
				typeof(void),
				builders.CppInitParams);
			
			// C++ init body
			AppendCppInitBody(
				funcName,
				funcNameLower,
				builders.CppInitBody);
		}
		
		static void AppendCppTemplateDeclaration(
			string typeName,
			string typeNamespace,
			int numTypeParameters,
			StringBuilder output)
		{
			int indent = AppendNamespaceBeginning(
				typeNamespace,
				output);
			AppendIndent(
				indent,
				output);
			AppendCppTemplateTypenames(
				numTypeParameters,
				output);
			output.Append("struct ");
			AppendTypeNameWithoutGenericSuffix(
				typeName,
				output);
			output.Append(";");
			output.Append('\n');
			AppendNamespaceEnding(
				indent,
				output);
			output.Append('\n');
		}
		
		static int AppendCppTypeDeclaration(
			string typeNamespace,
			string typeName,
			bool isStatic,
			Type[] typeParams,
			StringBuilder output)
		{
			int indent = AppendNamespaceBeginning(
				typeNamespace,
				output);
			AppendIndent(indent, output);
			if (isStatic)
			{
				output.Append("namespace ");
				AppendTypeNameWithoutGenericSuffix(
					typeName,
					output);
				output.Append('\n');
				AppendIndent(indent, output);
				output.Append("{\n");
				AppendIndent(indent, output);
				output.Append('}');
			}
			else
			{
				if (typeParams != null)
				{
					output.Append("template<> ");
				}
				output.Append("struct ");
				AppendTypeNameWithoutGenericSuffix(
					typeName,
					output);
				AppendCppTypeParameters(
					typeParams,
					output);
				output.Append(";");
			}
			output.Append('\n');
			AppendNamespaceEnding(
				indent,
				output);
			output.Append('\n');
			return indent;
		}
		
		static void AppendCppTypeDefinitionBegin(
			string typeName,
			string typeNamespace,
			TypeKind typeKind,
			Type[] typeParams,
			string baseTypeName,
			string baseTypeNamespace,
			Type[] baseTypeTypeParams,
			bool isStatic,
			int indent,
			StringBuilder output)
		{
			AppendNamespaceBeginning(
				typeNamespace,
				output);
			AppendIndent(
				indent,
				output);
			if (isStatic)
			{
				output.Append("namespace ");
				AppendTypeNameWithoutGenericSuffix(
					typeName,
					output);
			}
			else
			{
				if (typeParams != null)
				{
					output.Append("template<> ");
				}
				output.Append("struct ");
				AppendTypeNameWithoutGenericSuffix(
					typeName,
					output);
				AppendCppTypeParameters(typeParams, output);
				if (baseTypeName != null)
				{
					switch (typeKind)
					{
						case TypeKind.Class:
						case TypeKind.ManagedStruct:
							output.Append(" : ");
							AppendCppTypeName(
								baseTypeNamespace,
								baseTypeName,
								output);
							AppendCppTypeParameters(
								baseTypeTypeParams,
								output);
							break;
					}
				}
			}
			output.Append('\n');
			AppendIndent(
				indent,
				output);
			output.Append("{\n");
			if (!isStatic)
			{
				switch (typeKind)
				{
					case TypeKind.Class:
					case TypeKind.ManagedStruct:
						// Constructor from nullptr
						AppendIndent(indent + 1, output);
						AppendTypeNameWithoutGenericSuffix(
							typeName,
							output);
						AppendCppTypeParameters(
							typeParams,
							output);
						output.Append("(decltype(nullptr) n);\n");
						
						// Constructor from handle
						AppendIndent(indent + 1, output);
						AppendTypeNameWithoutGenericSuffix(
							typeName,
							output);
						AppendCppTypeParameters(
							typeParams,
							output);
						output.Append(
							"(Plugin::InternalUse iu, int32_t handle);\n");
						
						// Copy constructor
						AppendIndent(indent + 1, output);
						AppendTypeNameWithoutGenericSuffix(
							typeName,
							output);
						AppendCppTypeParameters(
							typeParams,
							output);
						output.Append("(const ");
						AppendTypeNameWithoutGenericSuffix(
							typeName,
							output);
						AppendCppTypeParameters(
							typeParams,
							output);
						output.Append("& other);\n");
						
						// Move constructor
						AppendIndent(indent + 1, output);
						AppendTypeNameWithoutGenericSuffix(
							typeName,
							output);
						AppendCppTypeParameters(
							typeParams,
							output);
						output.Append('(');
						AppendTypeNameWithoutGenericSuffix(
							typeName,
							output);
						AppendCppTypeParameters(
							typeParams,
							output);
						output.Append("&& other);\n");
						
						// Destructor
						AppendIndent(indent + 1, output);
						output.Append("virtual ~");
						AppendTypeNameWithoutGenericSuffix(
							typeName,
							output);
						AppendCppTypeParameters(
							typeParams,
							output);
						output.Append("();\n");
						
						// Assignment operator to same type
						AppendIndent(indent + 1, output);
						AppendTypeNameWithoutGenericSuffix(
							typeName,
							output);
						AppendCppTypeParameters(
							typeParams,
							output);
						output.Append("& operator=(const ");
						AppendTypeNameWithoutGenericSuffix(
							typeName,
							output);
						AppendCppTypeParameters(
							typeParams,
							output);
						output.Append("& other);\n");
						
						// Assignment operator to nullptr
						AppendIndent(indent + 1, output);
						AppendTypeNameWithoutGenericSuffix(
							typeName,
							output);
						AppendCppTypeParameters(
							typeParams,
							output);
						output.Append("& operator=(decltype(nullptr) other);\n");
						
						// Move assignment operator to same type
						AppendIndent(indent + 1, output);
						AppendTypeNameWithoutGenericSuffix(
							typeName,
							output);
						AppendCppTypeParameters(
							typeParams,
							output);
						output.Append("& operator=(");
						AppendTypeNameWithoutGenericSuffix(
							typeName,
							output);
						AppendCppTypeParameters(
							typeParams,
							output);
						output.Append("&& other);\n");
						
						// Equality operator with same type
						AppendIndent(indent + 1, output);
						output.Append("bool operator==(const ");
						AppendTypeNameWithoutGenericSuffix(
							typeName,
							output);
						AppendCppTypeParameters(
							typeParams,
							output);
						output.Append("& other) const;\n");
						
						// Inequality operator with same type
						AppendIndent(indent + 1, output);
						output.Append("bool operator!=(const ");
						AppendTypeNameWithoutGenericSuffix(
							typeName,
							output);
						AppendCppTypeParameters(
							typeParams,
							output);
						output.Append("& other) const;\n");
						break;
				}
			}
		}
		
		static void AppendCppTypeDefinitionEnd(
			bool isStatic,
			int indent,
			StringBuilder output)
		{
			AppendIndent(
				indent,
				output);
			output.Append('}');
			if (!isStatic)
			{
				output.Append(';');
			}
			output.Append('\n');
			AppendNamespaceEnding(
				indent,
				output);
			output.Append('\n');
		}
		
		static int AppendCppMethodDefinitionsBegin(
			string enclosingTypeName,
			string enclosingTypeNamespace,
			TypeKind enclosingTypeKind,
			Type[] enclosingTypeParams,
			string baseTypeName,
			string baseTypeNamespace,
			Type[] baseTypeTypeParams,
			bool isStatic,
			Action<int, string> extraDefault,
			Action<int, string> extraCopy,
			int indent,
			StringBuilder output)
		{
			int cppMethodDefinitionsIndent = AppendNamespaceBeginning(
				enclosingTypeNamespace,
				output);
			if (!isStatic && (
				enclosingTypeKind == TypeKind.Class
				|| enclosingTypeKind == TypeKind.ManagedStruct))
			{
				if (baseTypeName == null)
				{
					baseTypeName = "Object";
					baseTypeNamespace = "System";
				}
				
				// Construct with nullptr
				AppendIndent(indent, output);
				AppendTypeNameWithoutGenericSuffix(
					enclosingTypeName,
					output);
				AppendCppTypeParameters(
					enclosingTypeParams,
					output);
				output.Append("::");
				AppendTypeNameWithoutGenericSuffix(
					enclosingTypeName,
					output);
				output.Append("(decltype(nullptr) n)\n");
				AppendIndent(indent, output);
				output.Append("\t: ");
				AppendTypeNameWithoutGenericSuffix(
					enclosingTypeName,
					output);
				output.Append("(Plugin::InternalUse::Only, 0)\n");
				AppendIndent(indent, output);
				output.Append("{\n");
				extraDefault(indent + 1, "this->");
				AppendIndent(indent, output);
				output.Append("}\n");
				AppendIndent(indent, output);
				output.Append("\n");
				
				// Handle constructor
				AppendIndent(indent, output);
				AppendTypeNameWithoutGenericSuffix(
					enclosingTypeName,
					output);
				AppendCppTypeParameters(
					enclosingTypeParams,
					output);
				output.Append("::");
				AppendTypeNameWithoutGenericSuffix(
					enclosingTypeName,
					output);
				output.Append("(Plugin::InternalUse iu, int32_t handle)\n");
				AppendIndent(indent, output);
				output.Append("\t: ");
				AppendCppTypeName(
					baseTypeNamespace,
					baseTypeName,
					output);
				AppendCppTypeParameters(
					baseTypeTypeParams,
					output);
				output.Append("(iu, handle)\n");
				AppendIndent(indent, output);
				output.Append("{\n");
				AppendIndent(indent + 1, output);
				output.Append("if (handle)\n");
				AppendIndent(indent + 1, output);
				output.Append("{\n");
				AppendIndent(indent + 2, output);
				AppendReferenceManagedHandleFunctionCall(
					enclosingTypeName,
					enclosingTypeNamespace,
					enclosingTypeKind,
					enclosingTypeParams,
					"handle",
					output);
				output.Append(";\n");
				AppendIndent(indent + 1, output);
				output.Append("}\n");
				extraDefault(indent + 1, "this->");
				AppendIndent(indent, output);
				output.Append("}\n");
				AppendIndent(indent, output);
				output.Append("\n");
				
				// Copy constructor
				AppendIndent(indent, output);
				AppendTypeNameWithoutGenericSuffix(
					enclosingTypeName,
					output);
				AppendCppTypeParameters(
					enclosingTypeParams,
					output);
				output.Append("::");
				AppendTypeNameWithoutGenericSuffix(
					enclosingTypeName,
					output);
				output.Append("(const ");
				AppendTypeNameWithoutGenericSuffix(
					enclosingTypeName,
					output);
				AppendCppTypeParameters(
					enclosingTypeParams,
					output);
				output.Append("& other)\n");
				AppendIndent(indent, output);
				output.Append("\t: ");
				AppendTypeNameWithoutGenericSuffix(
					enclosingTypeName,
					output);
				output.Append("(Plugin::InternalUse::Only, other.Handle)\n");
				AppendIndent(indent, output);
				output.Append("{\n");
				extraCopy(indent + 1, "other.");
				AppendIndent(indent, output);
				output.Append("}\n");
				AppendIndent(indent, output);
				output.Append("\n");
				
				// Move constructor
				AppendIndent(indent, output);
				AppendTypeNameWithoutGenericSuffix(
					enclosingTypeName,
					output);
				AppendCppTypeParameters(
					enclosingTypeParams,
					output);
				output.Append("::");
				AppendTypeNameWithoutGenericSuffix(
					enclosingTypeName,
					output);
				output.Append("(");
				AppendTypeNameWithoutGenericSuffix(
					enclosingTypeName,
					output);
				AppendCppTypeParameters(
					enclosingTypeParams,
					output);
				output.Append("&& other)\n");
				AppendIndent(indent, output);
				output.Append("\t: ");
				AppendTypeNameWithoutGenericSuffix(
					enclosingTypeName,
					output);
				output.Append("(Plugin::InternalUse::Only, other.Handle)\n");
				AppendIndent(indent, output);
				output.Append("{\n");
				AppendIndent(indent + 1, output);
				output.Append("other.Handle = 0;\n");
				extraCopy(indent + 1, "other.");
				extraDefault(indent + 1, "other.");
				AppendIndent(indent, output);
				output.Append("}\n");
				AppendIndent(indent, output);
				output.Append("\n");
				
				// Destructor
				AppendIndent(indent, output);
				AppendTypeNameWithoutGenericSuffix(
					enclosingTypeName,
					output);
				AppendCppTypeParameters(
					enclosingTypeParams,
					output);
				output.Append("::~");
				AppendTypeNameWithoutGenericSuffix(
					enclosingTypeName,
					output);
				AppendCppTypeParameters(
					enclosingTypeParams,
					output);
				output.Append("()\n");
				AppendIndent(indent, output);
				output.Append("{\n");
				AppendIndent(indent + 1, output);
				output.Append("if (Handle)\n");
				AppendIndent(indent + 1, output);
				output.Append("{\n");
				AppendIndent(indent + 2, output);
				AppendDereferenceManagedHandleFunctionCall(
					enclosingTypeName,
					enclosingTypeNamespace,
					enclosingTypeKind,
					enclosingTypeParams,
					"Handle",
					output);
				output.Append(";\n");
				AppendIndent(indent + 2, output);
				output.Append("Handle = 0;\n");
				AppendIndent(indent + 1, output);
				output.Append("}\n");
				AppendIndent(indent, output);
				output.Append("}\n");
				AppendIndent(indent, output);
				output.Append("\n");
				
				// Assignment operator to same type
				AppendIndent(indent, output);
				AppendTypeNameWithoutGenericSuffix(
					enclosingTypeName,
					output);
				AppendCppTypeParameters(
					enclosingTypeParams,
					output);
				output.Append("& ");
				AppendTypeNameWithoutGenericSuffix(
					enclosingTypeName,
					output);
				AppendCppTypeParameters(
					enclosingTypeParams,
					output);
				output.Append("::operator=(const ");
				AppendTypeNameWithoutGenericSuffix(
					enclosingTypeName,
					output);
				AppendCppTypeParameters(
					enclosingTypeParams,
					output);
				output.Append("& other)\n");
				AppendIndent(indent, output);
				output.Append("{\n");
				AppendSetHandle(
					enclosingTypeName,
					enclosingTypeNamespace,
					enclosingTypeKind,
					enclosingTypeParams,
					indent + 1,
					"this",
					"other.Handle",
					output);
				extraCopy(indent + 1, "other.");
				AppendIndent(indent + 1, output);
				output.Append("return *this;\n");
				AppendIndent(indent, output);
				output.Append("}\n");
				AppendIndent(indent, output);
				output.Append("\n");
				
				// Assignment operator to nullptr
				AppendIndent(indent, output);
				AppendTypeNameWithoutGenericSuffix(
					enclosingTypeName,
					output);
				AppendCppTypeParameters(
					enclosingTypeParams,
					output);
				output.Append("& ");
				AppendTypeNameWithoutGenericSuffix(
					enclosingTypeName,
					output);
				AppendCppTypeParameters(
					enclosingTypeParams,
					output);
				output.Append("::operator=(decltype(nullptr) other)\n");
				AppendIndent(indent, output);
				output.Append("{\n");
				AppendIndent(indent + 1, output);
				output.Append("if (Handle)\n");
				AppendIndent(indent + 1, output);
				output.Append("{\n");
				AppendIndent(indent + 2, output);
				AppendDereferenceManagedHandleFunctionCall(
					enclosingTypeName,
					enclosingTypeNamespace,
					enclosingTypeKind,
					enclosingTypeParams,
					"Handle",
					output);
				output.Append(";\n");
				AppendIndent(indent + 2, output);
				output.Append("Handle = 0;\n");
				AppendIndent(indent + 1, output);
				output.Append("}\n");
				AppendIndent(indent + 1, output);
				output.Append("return *this;\n");
				AppendIndent(indent, output);
				output.Append("}\n");
				AppendIndent(indent, output);
				output.Append("\n");
				
				// Move assignment operator to same type
				AppendIndent(indent, output);
				AppendTypeNameWithoutGenericSuffix(
					enclosingTypeName,
					output);
				AppendCppTypeParameters(
					enclosingTypeParams,
					output);
				output.Append("& ");
				AppendTypeNameWithoutGenericSuffix(
					enclosingTypeName,
					output);
				AppendCppTypeParameters(
					enclosingTypeParams,
					output);
				output.Append("::operator=(");
				AppendTypeNameWithoutGenericSuffix(
					enclosingTypeName,
					output);
				AppendCppTypeParameters(
					enclosingTypeParams,
					output);
				output.Append("&& other)\n");
				AppendIndent(indent, output);
				output.Append("{\n");
				AppendIndent(indent + 1, output);
				output.Append("if (Handle)\n");
				AppendIndent(indent + 1, output);
				output.Append("{\n");
				AppendIndent(indent + 2, output);
				AppendDereferenceManagedHandleFunctionCall(
					enclosingTypeName,
					enclosingTypeNamespace,
					enclosingTypeKind,
					enclosingTypeParams,
					"Handle",
					output);
				output.Append(";\n");
				AppendIndent(indent + 1, output);
				output.Append("}\n");
				AppendIndent(indent + 1, output);
				output.Append("Handle = other.Handle;\n");
				extraCopy(indent + 1, "other.");
				AppendIndent(indent + 1, output);
				output.Append("other.Handle = 0;\n");
				extraDefault(indent + 1, "other.");
				AppendIndent(indent + 1, output);
				output.Append("return *this;\n");
				AppendIndent(indent, output);
				output.Append("}\n");
				AppendIndent(indent, output);
				output.Append('\n');
				
				// Equality operator with same type
				AppendIndent(indent, output);
				output.Append("bool ");
				AppendTypeNameWithoutGenericSuffix(
					enclosingTypeName,
					output);
				AppendCppTypeParameters(
					enclosingTypeParams,
					output);
				output.Append("::operator==(const ");
				AppendTypeNameWithoutGenericSuffix(
					enclosingTypeName,
					output);
				AppendCppTypeParameters(
					enclosingTypeParams,
					output);
				output.Append("& other) const\n");
				AppendIndent(indent, output);
				output.Append("{\n");
				AppendIndent(indent + 1, output);
				output.Append("return Handle == other.Handle;\n");
				AppendIndent(indent, output);
				output.Append("}\n");
				AppendIndent(indent, output);
				output.Append('\n');
				
				// Inequality operator with same type
				AppendIndent(indent, output);
				output.Append("bool ");
				AppendTypeNameWithoutGenericSuffix(
					enclosingTypeName,
					output);
				AppendCppTypeParameters(
					enclosingTypeParams,
					output);
				output.Append("::operator!=(const ");
				AppendTypeNameWithoutGenericSuffix(
					enclosingTypeName,
					output);
				AppendCppTypeParameters(
					enclosingTypeParams,
					output);
				output.Append("& other) const\n");
				AppendIndent(indent, output);
				output.Append("{\n");
				AppendIndent(indent + 1, output);
				output.Append("return Handle != other.Handle;\n");
				AppendIndent(indent, output);
				output.Append("}\n");
				AppendIndent(indent, output);
				output.Append('\n');
			}
			return cppMethodDefinitionsIndent;
		}
		
		static void AppendSetHandle(
			string enclosingTypeName,
			string enclosingTypeNamespace,
			TypeKind enclosingTypeKind,
			Type[] enclosingTypeParams,
			int indent,
			string thisExpression,
			string otherHandleExpression,
			StringBuilder output)
		{
			string thisHandleExpression = thisExpression + "->Handle";
			AppendIndent(indent, output);
			output.Append("if (");
			output.Append(thisHandleExpression);
			output.Append(")\n");
			AppendIndent(indent, output);
			output.Append("{\n");
			AppendIndent(indent + 1, output);
			AppendDereferenceManagedHandleFunctionCall(
				enclosingTypeName,
				enclosingTypeNamespace,
				enclosingTypeKind,
				enclosingTypeParams,
				thisHandleExpression,
				output);
			output.Append(";\n");
			AppendIndent(indent, output);
			output.Append("}\n");
			AppendIndent(indent, output);
			output.Append(thisHandleExpression);
			output.Append(" = ");
			output.Append(otherHandleExpression);
			output.Append(";\n");
			AppendIndent(indent, output);
			output.Append("if (");
			output.Append(thisHandleExpression);
			output.Append(")\n");
			AppendIndent(indent, output);
			output.Append("{\n");
			AppendIndent(indent + 1, output);
			AppendReferenceManagedHandleFunctionCall(
				enclosingTypeName,
				enclosingTypeNamespace,
				enclosingTypeKind,
				enclosingTypeParams,
				thisHandleExpression,
				output);
			output.Append(";\n");
			AppendIndent(indent, output);
			output.Append("}\n");
		}
		
		static void AppendReferenceManagedHandleFunctionCall(
			string enclosingTypeName,
			string enclosingTypeNamespace,
			TypeKind enclosingTypeKind,
			Type[] enclosingTypeParams,
			string handleVariable,
			StringBuilder output)
		{
			if (enclosingTypeKind == TypeKind.ManagedStruct)
			{
				output.Append("Plugin::ReferenceManaged");
				AppendReleaseFunctionNameSuffix(
					enclosingTypeName,
					enclosingTypeNamespace,
					enclosingTypeParams,
					output);
				output.Append("(Handle)");
			}
			else
			{
				output.Append("Plugin::ReferenceManagedClass(");
				output.Append(handleVariable);
				output.Append(")");
			}
		}
		
		static void AppendDereferenceManagedHandleFunctionCall(
			string enclosingTypeName,
			string enclosingTypeNamespace,
			TypeKind enclosingTypeKind,
			Type[] enclosingTypeParams,
			string handleVariable,
			StringBuilder output)
		{
			if (enclosingTypeKind == TypeKind.ManagedStruct)
			{
				output.Append("Plugin::DereferenceManaged");
				AppendReleaseFunctionNameSuffix(
					enclosingTypeName,
					enclosingTypeNamespace,
					enclosingTypeParams,
					output);
				output.Append("(Handle)");
			}
			else
			{
				output.Append("Plugin::DereferenceManagedClass(");
				output.Append(handleVariable);
				output.Append(")");
			}
		}
		
		static void AppendCppMethodDefinitionsEnd(
			int indent,
			StringBuilder output)
		{
			RemoveTrailingChars(output);
			output.Append('\n');
			AppendNamespaceEnding(
				indent,
				output);
			output.Append('\n');
		}
		
		static int AppendNamespaceBeginning(
			string namespaceName,
			StringBuilder output)
		{
			int startIndex = 0;
			int indent = 0;
			do
			{
				int separatorIndex = namespaceName.IndexOf(
					'.',
					startIndex);
				int endIndex = separatorIndex < 0
					? namespaceName.Length - 1
					: separatorIndex - 1;
				int len = 1 + endIndex - startIndex;
				AppendIndent(indent, output);
				output.Append("namespace ");
				output.Append(namespaceName, startIndex, len);
				output.Append('\n');
				AppendIndent(indent, output);
				output.Append("{\n");
				if (separatorIndex < 0)
				{
					break;
				}
				startIndex = separatorIndex + 1;
				indent++;
			}
			while (true);
			return indent + 1;
		}
		
		static void AppendNamespaceEnding(
			int indent,
			StringBuilder output)
		{
			indent--;
			for (; indent >= 0; --indent)
			{
				AppendIndent(indent, output);
				output.Append("}\n");
			}
		}
		
		static void AppendIndent(
			int indent,
			StringBuilder output)
		{
			output.Append('\t', indent);
		}
		
		static void AppendCsharpInitParam(
			string funcName,
			StringBuilder output)
		{
			output.Append("\t\t\tIntPtr ");
			output.Append(funcName);
			output.Append(",\n");
		}
		
		static void AppendCsharpInitCallArg(
			string funcName,
			StringBuilder output)
		{
			output.Append(
				"\t\t\t\tMarshal.GetFunctionPointerForDelegate(new ");
			output.Append(funcName);
			output.Append("Delegate(");
			output.Append(funcName);
			output.Append(")),\n");
		}
		
		static void AppendCsharpDelegateType(
			string funcName,
			bool isStatic,
			Type enclosingType,
			TypeKind enclosingTypeKind,
			Type returnType,
			ParameterInfo[] parameters,
			StringBuilder output)
		{
			output.Append("\t\tdelegate ");
			
			// Return type
			if (IsFullValueType(returnType))
			{
				AppendCsharpTypeName(
					returnType,
					output);
			}
			else
			{
				output.Append("int");
			}
			
			output.Append(' ');
			output.Append(funcName);
			output.Append("Delegate(");
			if (!isStatic)
			{
				if (enclosingTypeKind == TypeKind.FullStruct)
				{
					output.Append("ref ");
					AppendCsharpTypeName(
						enclosingType,
						output);
					output.Append(" thiz");
				}
				else
				{
					output.Append("int thisHandle");
				}
				if (parameters.Length > 0)
				{
					output.Append(", ");
				}
			}
			AppendCsharpParameterDeclaration(
				parameters,
				output);
			output.Append(");\n");
		}
		
		static void AppendCsharpFunctionBeginning(
			Type enclosingType,
			string funcName,
			bool isStatic,
			TypeKind enclosingTypeKind,
			Type returnType,
			ParameterInfo[] parameters,
			StringBuilder output)
		{
			output.Append("\t\t[MonoPInvokeCallback(typeof(");
			output.Append(funcName);
			output.Append("Delegate))]\n\t\tstatic ");
			
			// Return type
			if (returnType != null)
			{
				if (IsFullValueType(returnType))
				{
					AppendCsharpTypeName(
						returnType,
						output);
				}
				else
				{
					output.Append("int");
				}
				output.Append(' ');
			}
			
			// Function name
			output.Append(funcName);
			
			// Parameters
			output.Append("(");
			if (!isStatic)
			{
				if (enclosingTypeKind == TypeKind.FullStruct)
				{
					output.Append("ref ");
					AppendCsharpTypeName(
						enclosingType,
						output);
					output.Append(" thiz");
				}
				else
				{
					output.Append("int thisHandle");
				}
				if (parameters.Length > 0)
				{
					output.Append(", ");
				}
			}
			AppendCsharpParameterDeclaration(
				parameters,
				output);
			output.Append(")\n\t\t{\n\t\t\t");
			
			// Start try/catch block
			output.Append("try\n\t\t\t{\n\t\t\t\t");
			
			// Get "this"
			if (!isStatic
				&& enclosingTypeKind != TypeKind.FullStruct)
			{
				output.Append("var thiz = (");
				AppendCsharpTypeName(
					enclosingType,
					output);
				output.Append(')');
				AppendHandleStoreTypeName(
					enclosingType,
					output);
				output.Append(
					".Get(thisHandle);\n\t\t\t\t");
			}
			
			// Get managed type params from ObjectStore
			foreach (ParameterInfo param in parameters)
			{
				Type paramType = param.DereferencedParameterType;
				if (param.Kind == TypeKind.Class
					|| param.Kind == TypeKind.ManagedStruct)
				{
					output.Append("var ");
					output.Append(param.Name);
					output.Append(" = ");
					if (paramType != typeof(object))
					{
						output.Append('(');
						AppendCsharpTypeName(paramType, output);
						output.Append(')');
					}
					AppendHandleStoreTypeName(paramType, output);
					output.Append(".Get(");
					output.Append(param.Name);
					output.Append("Handle);\n\t\t\t\t");
				}
			}
			
			// Save return value as local variable
			if (returnType != typeof(void))
			{
				output.Append("var returnValue = ");
			}
		}
		
		static void AppendCsharpFunctionCallSubject(
			Type enclosingType,
			bool isStatic,
			StringBuilder output)
		{
			if (isStatic)
			{
				AppendCsharpTypeName(
					enclosingType,
					output);
			}
			else
			{
				output.Append("thiz");
			}
		}
		
		static void AppendCsharpFunctionCallParameters(
			ParameterInfo[] parameters,
			StringBuilder output)
		{
			output.Append('(');
			for (int i = 0; i < parameters.Length; ++i)
			{
				ParameterInfo param = parameters[i];
				if (param.IsOut)
				{
					output.Append("out ");
				}
				else if (param.IsRef)
				{
					output.Append("ref ");
				}
				output.Append(param.Name);
				if (i != parameters.Length - 1)
				{
					output.Append(", ");
				}
			}
			output.Append(')');
		}
		
		static void AppendStructStoreReplace(
			Type enclosingType,
			string handleVariable,
			string structVariable,
			StringBuilder output)
		{
			output.Append("\n\t\t\t\t");
			AppendHandleStoreTypeName(
				enclosingType,
				output);
			output.Append(".Replace(");
			output.Append(handleVariable);
			output.Append(", ref ");
			output.Append(structVariable);
			output.Append(");");
		}
		
		static void AppendCsharpFunctionReturn(
			ParameterInfo[] parameters,
			Type returnType,
			TypeKind returnTypeKind,
			Type[] exceptionTypes,
			bool forceReturnReturnValue,
			StringBuilder output)
		{
			// Store reference out and ref params and overwrite handles
			foreach (ParameterInfo param in parameters)
			{
				if ((param.Kind == TypeKind.Class
					|| param.Kind == TypeKind.ManagedStruct)
					&& (param.IsOut || param.IsRef))
				{
					output.Append("\n\t\t\t\tint ");
					output.Append(param.Name);
					output.Append("HandleNew = ");
					AppendHandleStoreTypeName(
						param.DereferencedParameterType,
						output);
					output.Append('.');
					if (param.Kind == TypeKind.ManagedStruct)
					{
						output.Append("Store");
					}
					else
					{
						output.Append("GetHandle");
					}
					output.Append('(');
					output.Append(param.Name);
					output.Append(");\n\t\t\t\t");
					output.Append(param.Name);
					output.Append("Handle = ");
					output.Append(param.Name);
					output.Append("HandleNew;");
				}
			}
			
			// Return
			if (returnType != typeof(void))
			{
				output.Append("\n\t\t\t\treturn ");
				if (
					forceReturnReturnValue
					|| returnTypeKind == TypeKind.Enum
					|| returnTypeKind == TypeKind.FullStruct
					|| returnTypeKind == TypeKind.Primitive)
				{
					output.Append("returnValue");
				}
				else
				{
					AppendHandleStoreTypeName(
						returnType,
						output);
					output.Append('.');
					if (returnTypeKind == TypeKind.Class)
					{
						output.Append("GetHandle");
					}
					else
					{
						output.Append("Store");
					}
					output.Append("(returnValue)");
				}
				output.Append(';');
			}
			
			// Returning ends the function
			AppendCsharpFunctionEnd(
				returnType,
				exceptionTypes,
				output);
		}
		
		static void AppendCsharpFunctionEnd(
			Type returnType,
			Type[] exceptionTypes,
			StringBuilder output)
		{
			output.Append('\n');
			output.Append("\t\t\t}\n");
			if (exceptionTypes == null
				|| Array.IndexOf(
				exceptionTypes,
				typeof(NullReferenceException)) < 0)
			{
				AppendCsharpCatchException(
					typeof(NullReferenceException),
					returnType,
					output);
			}
			if (exceptionTypes != null)
			{
				foreach (Type exceptionType in exceptionTypes)
				{
					AppendCsharpCatchException(
						exceptionType,
						returnType,
						output);
				}
			}
			AppendCsharpCatchException(
				typeof(Exception),
				returnType,
				output);
			output.Append("\t\t}\n");
			output.Append("\t\t\n");
		}
		
		static void AppendCsharpCatchException(
			Type exceptionType,
			Type returnType,
			StringBuilder output)
		{
			output.Append("\t\t\tcatch (");
			AppendCsharpTypeName(
				exceptionType,
				output);
			output.Append(" ex)\n");
			output.Append("\t\t\t{\n");
			output.Append("\t\t\t\tUnityEngine.Debug.LogException(ex);\n");
			output.Append("\t\t\t\tNativeScript.Bindings.");
			AppendCsharpSetCsharpExceptionFunctionName(
				exceptionType,
				output);
			output.Append("(NativeScript.Bindings.ObjectStore.Store(ex));\n");
			if (returnType != typeof(void))
			{
				output.Append("\t\t\t\treturn default(");
				if (IsFullValueType(returnType))
				{
					AppendCsharpTypeName(
						returnType,
						output);
				}
				else
				{
					output.Append("int");
				}
				output.Append(");\n");
			}
			output.Append("\t\t\t}\n");
		}
		
		static void AppendCsharpSetCsharpExceptionFunctionName(
			Type exceptionType,
			StringBuilder output
		)
		{
			output.Append("SetCsharpException");
			if (exceptionType != typeof(Exception))
			{
				AppendNamespace(
					exceptionType.Namespace,
					string.Empty,
					output);
				AppendTypeNameWithoutGenericSuffix(
					exceptionType.Name,
					output);
			}
		}
		
		static void AppendCsharpParameterDeclaration(
			ParameterInfo[] parameters,
			StringBuilder output)
		{
			for (int i = 0; i < parameters.Length; ++i)
			{
				ParameterInfo param = parameters[i];
				
				// out or ref qualifiers if necessary
				switch (param.Kind)
				{
					case TypeKind.FullStruct:
						if (param.IsOut)
						{
							output.Append("out ");
						}
						else
						{
							output.Append("ref ");
						}
						break;
					case TypeKind.ManagedStruct:
					case TypeKind.Primitive:
					case TypeKind.Enum:
					case TypeKind.Class:
						if (param.IsOut || param.IsRef)
						{
							output.Append("ref ");
						}
						break;
				}
				
				// Param type- int for handles
				switch (param.Kind)
				{
					case TypeKind.ManagedStruct:
					case TypeKind.Class:
						output.Append("int");
						break;
					default:
						AppendCsharpTypeName(
							param.DereferencedParameterType,
							output);
						break;
				}
				
				// Param name
				output.Append(' ');
				output.Append(param.Name);
				
				// Handle suffix if necessary
				if (param.Kind == TypeKind.Class
					|| param.Kind == TypeKind.ManagedStruct)
				{
					output.Append("Handle");
				}
				
				if (i != parameters.Length - 1)
				{
					output.Append(", ");
				}
			}
		}
		
		static void AppendCppParameterDeclaration(
			ParameterInfo[] parameters,
			StringBuilder output)
		{
			for (int i = 0; i < parameters.Length; ++i)
			{
				ParameterInfo param = parameters[i];
				
				AppendCppTypeName(
					param.DereferencedParameterType,
					output);
				
				// Pointer (*) or reference (&) suffix if necessary
				if (param.IsOut || param.IsRef)
				{
					output.Append('*');
				}
				else if (
					param.Kind == TypeKind.FullStruct ||
					param.Kind == TypeKind.ManagedStruct ||
					param.Kind == TypeKind.Class ||
					param.IsVirtual)
				{
					output.Append('&');
				}
				
				// Param name
				output.Append(' ');
				output.Append(param.Name);
				
				if (i != parameters.Length - 1)
				{
					output.Append(", ");
				}
			}
		}

		static void AppendCppInitBody(
			string globalVariableName,
			string paramName,
			StringBuilder output)
		{
			output.Append("\tPlugin::");
			output.Append(globalVariableName);
			output.Append(" = ");
			output.Append(paramName);
			output.Append(";\n");
		}
		
		static void AppendCppMethodDefinitionBegin(
			string enclosingTypeName,
			Type returnType,
			string methodName,
			Type[] typeTypeParams,
			Type[] methodTypeParams,
			ParameterInfo[] parameters,
			int indent,
			StringBuilder output)
		{
			// Indent
			AppendIndent(
				indent,
				output);
			
			// Template
			if (methodTypeParams != null)
			{
				output.Append("template<> ");
			}
			
			// Return type
			if (returnType != null)
			{
				AppendCppTypeName(
					returnType,
					output);
				output.Append(' ');
			}
			
			// Type name
			AppendTypeNameWithoutGenericSuffix(
				enclosingTypeName,
				output);
			AppendCppTypeParameters(
				typeTypeParams,
				output);
			output.Append("::");
			
			// Method name
			AppendTypeNameWithoutGenericSuffix(
				methodName,
				output);
			
			// Template parameters
			AppendCppTypeParameters(
				methodTypeParams,
				output);
			
			// Parameters
			output.Append('(');
			AppendCppParameterDeclaration(
				parameters,
				output);
			output.Append(")\n");
		}
		
		static void AppendCppMethodReturn(
			Type returnType,
			TypeKind returnTypeKind,
			int indent,
			StringBuilder output)
		{
			if (returnType != null && returnType != typeof(void))
			{
				AppendIndent(indent, output);
				output.Append("return ");
				switch (returnTypeKind)
				{
					case TypeKind.Enum:
					case TypeKind.FullStruct:
					case TypeKind.Primitive:
						output.Append("returnValue");
						break;
					default:
						AppendCppTypeName(
							returnType,
							output);
						output.Append("(Plugin::InternalUse::Only, returnValue)");
						break;
				}
				output.Append(";\n");
			}
		}
		
		static void AppendCppPluginFunctionCall(
			bool isStatic,
			string enclosingTypeName,
			string enclosingTypeNamespace,
			TypeKind enclosingTypeKind,
			Type[] enclosingTypeParams,
			Type returnType,
			string funcName,
			ParameterInfo[] parameters,
			int indent,
			StringBuilder output)
		{
			// Gather handles for out and ref parameters
			foreach (ParameterInfo param in parameters)
			{
				if ((param.Kind == TypeKind.Class
					|| param.Kind == TypeKind.ManagedStruct)
					&& (param.IsOut || param.IsRef))
				{
					AppendIndent(indent, output);
					output.Append("int32_t ");
					output.Append(param.Name);
					output.Append("Handle = ");
					output.Append(param.Name);
					output.Append("->Handle;\n");
				}
			}
			
			// Call the function
			AppendIndent(indent, output);
			if (returnType != null && returnType != typeof(void))
			{
				output.Append("auto returnValue = ");
			}
			output.Append("Plugin::");
			output.Append(funcName);
			output.Append("(");
			if (!isStatic)
			{
				if (enclosingTypeKind == TypeKind.FullStruct)
				{
					output.Append("this");
				}
				else
				{
					output.Append("Handle");
				}
				if (parameters.Length > 0)
				{
					output.Append(", ");
				}
			}
			for (int i = 0; i < parameters.Length; ++i)
			{
				ParameterInfo param = parameters[i];
				switch (param.Kind)
				{
					case TypeKind.FullStruct:
					case TypeKind.Primitive:
					case TypeKind.Enum:
						output.Append(param.Name);
						break;
					default:
						if (param.IsOut || param.IsRef)
						{
							output.Append('&');
							output.Append(param.Name);
						}
						else
						{
							output.Append(param.Name);
							output.Append('.');
						}
						output.Append("Handle");
						break;
				}
				if (i != parameters.Length - 1)
				{
					output.Append(", ");
				}
			}
			output.Append(");\n");
			
			AppendCppUnhandledExceptionHandling(
				indent,
				output);
			
			// Set out and ref parameters
			foreach (ParameterInfo param in parameters)
			{
				if ((param.Kind == TypeKind.Class
					|| param.Kind == TypeKind.ManagedStruct)
					&& (param.IsOut || param.IsRef))
				{
					AppendSetHandle(
						enclosingTypeName,
						enclosingTypeNamespace,
						enclosingTypeKind,
						enclosingTypeParams,
						indent,
						param.Name,
						param.Name + "Handle",
						output);
				}
			}
		}
		
		static void AppendCppUnhandledExceptionHandling(
			int indent,
			StringBuilder output)
		{
			AppendIndent(indent, output);
			output.Append("if (Plugin::unhandledCsharpException)\n");
			AppendIndent(indent, output);
			output.Append("{\n");
			AppendIndent(indent + 1, output);
			output.Append("System::Exception* ex = Plugin::unhandledCsharpException;\n");
			AppendIndent(indent + 1, output);
			output.Append("Plugin::unhandledCsharpException = nullptr;\n");
			AppendIndent(indent + 1, output);
			output.Append("ex->ThrowReferenceToThis();\n");
			AppendIndent(indent + 1, output);
			output.Append("delete ex;\n");
			AppendIndent(indent, output);
			output.Append("}\n");
		}
		
		static void AppendCppInitParam(
			string funcName,
			bool isStatic,
			string enclosingTypeName,
			string enclosingTypeNamespace,
			TypeKind enclosingTypeKind,
			ParameterInfo[] parameters,
			Type returnType,
			StringBuilder output
		)
		{
			output.Append('\t');
			AppendCppFunctionPointer(
				funcName,
				isStatic,
				enclosingTypeName,
				enclosingTypeNamespace,
				enclosingTypeKind,
				parameters,
				returnType,
				',',
				output
			);
			output.Append('\n');
		}
		
		static void AppendCppFunctionPointerDefinition(
			string funcName,
			bool isStatic,
			string enclosingTypeName,
			string enclosingTypeNamespace,
			TypeKind enclosingTypeKind,
			ParameterInfo[] parameters,
			Type returnType,
			StringBuilder output
		)
		{
			output.Append('\t');
			AppendCppFunctionPointer(
				funcName,
				isStatic,
				enclosingTypeName,
				enclosingTypeNamespace,
				enclosingTypeKind,
				parameters,
				returnType,
				';',
				output
			);
			output.Append('\n');
		}
		
		static void AppendCppFunctionPointer(
			string funcName,
			bool isStatic,
			string enclosingTypeName,
			string enclosingTypeNamespace,
			TypeKind enclosingTypeKind,
			ParameterInfo[] parameters,
			Type returnType,
			char separator,
			StringBuilder output)
		{
			// Return type
			if (IsFullValueType(returnType))
			{
				AppendCppTypeName(returnType, output);
			}
			else
			{
				output.Append("int32_t");
			}
			
			output.Append(" (*");
			output.Append(funcName);
			output.Append(")(");
			if (!isStatic)
			{
				switch (enclosingTypeKind)
				{
					case TypeKind.FullStruct:
					case TypeKind.Primitive:
						AppendCppTypeName(
							enclosingTypeNamespace,
							enclosingTypeName,
							output);
						output.Append("* thiz");
						break;
					default:
						output.Append("int32_t thisHandle");
						break;
				}
				if (parameters.Length > 0)
				{
					output.Append(", ");
				}
			}
			for (int i = 0; i < parameters.Length; ++i)
			{
				ParameterInfo param = parameters[i];
				switch (param.Kind)
				{
					case TypeKind.Primitive:
					case TypeKind.Enum:
						AppendCppTypeName(
							param.DereferencedParameterType,
							output);
						if (param.IsOut || param.IsRef)
						{
							output.Append('*');
						}
						break;
					case TypeKind.FullStruct:
						AppendCppTypeName(
							param.DereferencedParameterType,
							output);
						if (param.IsOut || param.IsRef)
						{
							output.Append('*');
						}
						else
						{
							output.Append('&');
						}
						break;
					case TypeKind.Class:
					case TypeKind.ManagedStruct:
						output.Append("int32_t");
						if (param.IsOut || param.IsRef)
						{
							output.Append('*');
						}
						break;
				}
				output.Append(' ');
				output.Append(param.Name);
				if (param.Kind == TypeKind.Class
					|| param.Kind == TypeKind.ManagedStruct)
				{
					output.Append("Handle");
				}
				if (i != parameters.Length - 1)
				{
					output.Append(", ");
				}
			}
			output.Append(')');
			output.Append(separator);
		}
		
		static void AppendCppTemplateTypenames(
			int numTypeParameters,
			StringBuilder output)
		{
			if (numTypeParameters > 0)
			{
				output.Append("template<");
				for (int i = 0; i < numTypeParameters; ++i)
				{
					output.Append("typename T");
					output.Append(i);
					if (i != numTypeParameters - 1)
					{
						output.Append(", ");
					}
				}
				output.Append("> ");
			}
		}
		
		static void AppendCppMethodDeclaration(
			string methodName,
			bool enclosingTypeIsStatic,
			bool methodIsVirtual,
			bool methodIsStatic,
			Type returnType,
			Type[] typeParameters,
			ParameterInfo[] parameters,
			StringBuilder output)
		{
			AppendCppTemplateTypenames(
				typeParameters == null ? 0 : typeParameters.Length,
				output);
			
			if (!enclosingTypeIsStatic && methodIsStatic)
			{
				output.Append("static ");
			}
			
			if (methodIsVirtual)
			{
				output.Append("virtual ");
			}
			
			// Return type
			if (returnType != null)
			{
				AppendCppTypeName(
					returnType,
					output);
				output.Append(' ');
			}
			
			// Method name might be a constructor/type name, so remove suffix
			// just in case
			AppendTypeNameWithoutGenericSuffix(
				methodName,
				output);
			
			// Parameters
			output.Append('(');
			AppendCppParameterDeclaration(
				parameters,
				output);
			output.Append(')');
			
			output.Append(";\n");
		}
		
		static void AppendCsharpTypeName(
			Type type,
			StringBuilder output)
		{
			if (type == typeof(void))
			{
				output.Append("void");
			}
			else if (type == typeof(bool))
			{
				output.Append("bool");
			}
			else if (type == typeof(sbyte))
			{
				output.Append("sbyte");
			}
			else if (type == typeof(byte))
			{
				output.Append("byte");
			}
			else if (type == typeof(short))
			{
				output.Append("short");
			}
			else if (type == typeof(ushort))
			{
				output.Append("ushort");
			}
			else if (type == typeof(int))
			{
				output.Append("int");
			}
			else if (type == typeof(uint))
			{
				output.Append("uint");
			}
			else if (type == typeof(long))
			{
				output.Append("long");
			}
			else if (type == typeof(ulong))
			{
				output.Append("ulong");
			}
			else if (type == typeof(char))
			{
				output.Append("char");
			}
			else if (type == typeof(float))
			{
				output.Append("float");
			}
			else if (type == typeof(double))
			{
				output.Append("double");
			}
			else if (type == typeof(string))
			{
				output.Append("string");
			}
			else if (type.IsArray)
			{
				AppendCsharpTypeName(
					type.GetElementType(),
					output);
				output.Append('[');
				output.Append(',', type.GetArrayRank()-1);
				output.Append(']');
			}
			else
			{
				output.Append(type.Namespace);
				output.Append('.');
				AppendTypeNameWithoutGenericSuffix(
					type.Name,
					output);
				Type[] genTypes = type.GetGenericArguments();
				AppendCSharpTypeParameters(
					genTypes,
					output);
			}
		}
		
		static void AppendCppTypeName(
			Type type,
			StringBuilder output)
		{
			if (type == typeof(void))
			{
				output.Append("void");
			}
			else if (type == typeof(bool))
			{
				output.Append("System::Boolean");
			}
			else if (type == typeof(sbyte))
			{
				output.Append("int8_t");
			}
			else if (type == typeof(byte))
			{
				output.Append("uint8_t");
			}
			else if (type == typeof(short))
			{
				output.Append("int16_t");
			}
			else if (type == typeof(ushort))
			{
				output.Append("uint16_t");
			}
			else if (type == typeof(int))
			{
				output.Append("int32_t");
			}
			else if (type == typeof(uint))
			{
				output.Append("uint32_t");
			}
			else if (type == typeof(long))
			{
				output.Append("int64_t");
			}
			else if (type == typeof(ulong))
			{
				output.Append("uint64_t");
			}
			else if (type == typeof(char))
			{
				output.Append("System::Char");
			}
			else if (type == typeof(float))
			{
				output.Append("float");
			}
			else if (type == typeof(double))
			{
				output.Append("double");
			}
			else if (type == typeof(string))
			{
				output.Append("System::String");
			}
			else if (type.IsArray)
			{
				int rank = type.GetArrayRank();
				output.Append("System::Array");
				output.Append(rank);
				output.Append('<');
				Type elementType = type.GetElementType();
				for (int i = 0; i < rank; ++i)
				{
					AppendCppTypeName(
						elementType,
						output);
					if (i != rank -1)
					{
						output.Append(", ");
					}
				}
				output.Append('>');
			}
			else if (typeof(Delegate).IsAssignableFrom(type))
			{
				AppendCppTypeName(
					type.Namespace,
					type.Name,
					output);
				Type[] genTypes = type.GetGenericArguments();
				if (genTypes.Length > 0)
				{
					output.Append(genTypes.Length);
				}
				AppendCppTypeParameters(
					genTypes,
					output);
			}
			else
			{
				AppendCppTypeName(
					type.Namespace,
					type.Name,
					output);
				Type[] genTypes = type.GetGenericArguments();
				AppendCppTypeParameters(
					genTypes,
					output);
			}
		}
		
		static void AppendCppTypeName(
			string namespaceName,
			string name,
			StringBuilder output)
		{
			AppendNamespace(namespaceName, "::", output);
			output.Append("::");
			AppendTypeNameWithoutGenericSuffix(
				name,
				output);
		}
		
		static void RemoveTrailingChars(
			StringBuilders builders)
		{
			RemoveTrailingChars(builders.CsharpInitParams);
			RemoveTrailingChars(builders.CsharpDelegateTypes);
			RemoveTrailingChars(builders.CsharpStructStoreInitCalls);
			RemoveTrailingChars(builders.CsharpInitCall);
			RemoveTrailingChars(builders.CsharpBaseTypes);
			RemoveTrailingChars(builders.CsharpFunctions);
			RemoveTrailingChars(builders.CsharpMonoBehaviours);
			RemoveTrailingChars(builders.CsharpDelegates);
			RemoveTrailingChars(builders.CsharpImports);
			RemoveTrailingChars(builders.CsharpGetDelegateCalls);
			RemoveTrailingChars(builders.CppFunctionPointers);
			RemoveTrailingChars(builders.CppTypeDeclarations);
			RemoveTrailingChars(builders.CppTypeDefinitions);
			RemoveTrailingChars(builders.CppMethodDefinitions);
			RemoveTrailingChars(builders.CppInitParams);
			RemoveTrailingChars(builders.CppInitBody);
			RemoveTrailingChars(builders.CppMonoBehaviourMessages);
			RemoveTrailingChars(builders.CppGlobalStateAndFunctions);
			RemoveTrailingChars(builders.CppBoxingMethodDeclarations);
		}
		
		// Remove trailing chars (e.g. commas) for last elements
		static void RemoveTrailingChars(
			StringBuilder builder)
		{
			int len = builder.Length;
			int i;
			for (i = len - 1; i >= 0; --i)
			{
				char cur = builder[i];
				switch (cur)
				{
					case '\n':
					case '\t':
					case ',':
						break;
					default:
						goto after;
				}
			}
			after:
			if (i < len - 1)
			{
				builder.Remove(i + 1, len - i - 1);
			}
		}
		
		static void InjectBuilders(
			StringBuilders builders)
		{
			// Inject into source files
			string csharpContents = File.ReadAllText(CsharpPath);
			string cppHeaderContents = File.ReadAllText(CppHeaderPath);
			string cppSourceContents = File.ReadAllText(CppSourcePath);
			csharpContents = InjectIntoString(
				csharpContents,
				"/*BEGIN INIT PARAMS*/\n",
				"\n\t\t\t/*END INIT PARAMS*/",
				builders.CsharpInitParams.ToString());
			csharpContents = InjectIntoString(
				csharpContents,
				"/*BEGIN DELEGATE TYPES*/\n",
				"\n\t\t/*END DELEGATE TYPES*/",
				builders.CsharpDelegateTypes.ToString());
			csharpContents = InjectIntoString(
				csharpContents,
				"/*BEGIN STRUCTSTORE INIT CALLS*/\n",
				"\n\t\t\t/*END STRUCTSTORE INIT CALLS*/",
				builders.CsharpStructStoreInitCalls.ToString());
			csharpContents = InjectIntoString(
				csharpContents,
				"/*BEGIN INIT CALL*/\n",
				"\n\t\t\t\t/*END INIT CALL*/",
				builders.CsharpInitCall.ToString());
			csharpContents = InjectIntoString(
				csharpContents,
				"/*BEGIN BASE TYPES*/\n",
				"\n\t\t/*END BASE TYPES*/",
				builders.CsharpBaseTypes.ToString());
			csharpContents = InjectIntoString(
				csharpContents,
				"/*BEGIN FUNCTIONS*/\n",
				"\n\t\t/*END FUNCTIONS*/",
				builders.CsharpFunctions.ToString());
			csharpContents = InjectIntoString(
				csharpContents,
				"/*BEGIN MONOBEHAVIOURS*/\n",
				"\n/*END MONOBEHAVIOURS*/",
				builders.CsharpMonoBehaviours.ToString());
			csharpContents = InjectIntoString(
				csharpContents,
				"/*BEGIN MONOBEHAVIOUR DELEGATES*/\n",
				"\n\t\t/*END MONOBEHAVIOUR DELEGATES*/",
				builders.CsharpDelegates.ToString());
			csharpContents = InjectIntoString(
				csharpContents,
				"/*BEGIN MONOBEHAVIOUR IMPORTS*/\n",
				"\n\t\t/*END MONOBEHAVIOUR IMPORTS*/",
				builders.CsharpImports.ToString());
			csharpContents = InjectIntoString(
				csharpContents,
				"/*BEGIN MONOBEHAVIOUR GETDELEGATE CALLS*/\n",
				"\n\t\t\t/*END MONOBEHAVIOUR GETDELEGATE CALLS*/",
				builders.CsharpGetDelegateCalls.ToString());
			cppSourceContents = InjectIntoString(
				cppSourceContents,
				"/*BEGIN FUNCTION POINTERS*/\n",
				"\n\t/*END FUNCTION POINTERS*/",
				builders.CppFunctionPointers.ToString());
			cppHeaderContents = InjectIntoString(
				cppHeaderContents,
				"/*BEGIN TYPE DECLARATIONS*/\n",
				"\n/*END TYPE DECLARATIONS*/",
				builders.CppTypeDeclarations.ToString());
			cppHeaderContents = InjectIntoString(
				cppHeaderContents,
				"/*BEGIN TYPE DEFINITIONS*/\n",
				"\n/*END TYPE DEFINITIONS*/",
				builders.CppTypeDefinitions.ToString());
			cppSourceContents = InjectIntoString(
				cppSourceContents,
				"/*BEGIN METHOD DEFINITIONS*/\n",
				"\n/*END METHOD DEFINITIONS*/",
				builders.CppMethodDefinitions.ToString());
			cppSourceContents = InjectIntoString(
				cppSourceContents,
				"/*BEGIN INIT PARAMS*/\n",
				"\n\t/*END INIT PARAMS*/",
				builders.CppInitParams.ToString());
			cppSourceContents = InjectIntoString(
				cppSourceContents,
				"/*BEGIN INIT BODY*/\n",
				"\n\t/*END INIT BODY*/",
				builders.CppInitBody.ToString());
			cppSourceContents = InjectIntoString(
				cppSourceContents,
				"/*BEGIN MONOBEHAVIOUR MESSAGES*/\n",
				"\n/*END MONOBEHAVIOUR MESSAGES*/",
				builders.CppMonoBehaviourMessages.ToString());
			cppSourceContents = InjectIntoString(
				cppSourceContents,
				"/*BEGIN GLOBAL STATE AND FUNCTIONS*/\n",
				"\n\t/*END GLOBAL STATE AND FUNCTIONS*/",
				builders.CppGlobalStateAndFunctions.ToString());
			cppHeaderContents = InjectIntoString(
				cppHeaderContents,
				"*BEGIN BOXING METHOD DECLARATIONS*/\n",
				"\n\t\t/*END BOXING METHOD DECLARATIONS*/",
				builders.CppBoxingMethodDeclarations.ToString());
			
			File.WriteAllText(CsharpPath, csharpContents);
			File.WriteAllText(CppHeaderPath, cppHeaderContents);
			File.WriteAllText(CppSourcePath, cppSourceContents);
		}
		
		static string InjectIntoString(
			string contents,
			string beginMarker,
			string endMarker,
			string text)
		{
			for (int startIndex = 0; ; )
			{
				int beginIndex = contents.IndexOf(beginMarker, startIndex);
				if (beginIndex < 0)
				{
					return contents;
				}
				int afterBeginIndex = beginIndex + beginMarker.Length;
				int endIndex = contents.IndexOf(endMarker, afterBeginIndex);
				if (endIndex < 0)
				{
					throw new Exception(
						string.Format(
							"No end ({0}) for begin ({1}) at {2} after {3}",
							endMarker,
							beginMarker,
							beginIndex,
							startIndex));
				}
				string begin = contents.Substring(0, afterBeginIndex);
				string end = contents.Substring(endIndex);
				contents = begin + text + end;
				startIndex = beginIndex + 1;
			}
		}
	}
}