﻿using Facepunch;
using Facepunch.Models;
using Newtonsoft.Json;
using Logger = Carbon.Logger;

/*
 *
 * Copyright (c) 2022-2023 Carbon Community
 * All rights reserved.
 *
 */

namespace Oxide.Core.Plugins
{
	[JsonObject(MemberSerialization.OptIn)]
	public class Plugin : BaseHookable, IDisposable
	{
		public PluginManager Manager { get; set; }

		public bool IsCorePlugin { get; set; }

		[JsonProperty]
		public string Title { get; set; }
		[JsonProperty]
		public string Description { get; set; }
		[JsonProperty]
		public string Author { get; set; }
		public int ResourceId { get; set; }
		public bool HasConfig { get; set; }
		public bool HasMessages { get; set; }
		public bool HasConditionals { get; set; }

		[JsonProperty]
		public double CompileTime { get; set; }
		[JsonProperty]
		public ModLoader.FailedMod.Error[] CompileWarnings { get; set; }

		[JsonProperty]
		public string FilePath { get; set; }
		[JsonProperty]
		public string FileName { get; set; }

		public string Filename => FileName;

		public override void TrackStart()
		{
			if (IsCorePlugin) return;

			base.TrackStart();
		}
		public override void TrackEnd()
		{
			if (IsCorePlugin) return;

			base.TrackEnd();
		}

		public Plugin[] Requires { get; set; }

		public ModLoader.ModPackage Package;
		public IBaseProcessor Processor;
		public IBaseProcessor.IProcess ProcessorProcess;

		public static implicit operator bool(Plugin other)
		{
			return other != null && other.IsLoaded;
		}

		public virtual bool IInit()
		{
			BuildHookCache(BindingFlags.NonPublic | BindingFlags.Instance);

			using (TimeMeasure.New($"Processing PluginReferences on '{this}'"))
			{
				if (!InternalApplyPluginReferences())
				{
					Logger.Warn($"Failed vibe check {ToString()}");
					return false;
				}
			}
			Carbon.Logger.Debug(Name, "Assigned plugin references");

			if (Hooks != null)
			{
				string requester = FileName is not default(string) ? FileName : $"{this}";
				using (TimeMeasure.New($"Processing Hooks on '{this}'"))
				{
					foreach (var hook in Hooks)
					{
						Community.Runtime.HookManager.Subscribe(HookStringPool.GetOrAdd(hook), requester);
					}

				}
				Carbon.Logger.Debug(Name, "Processed hooks");
			}

			CallHook("Init");

			TrackInit();

			return true;
		}
		internal virtual void ILoad()
		{
			using (TimeMeasure.New($"Load on '{this}'"))
			{
				IsLoaded = true;
				CallHook("OnLoaded");
				CallHook("Loaded");
			}

			using (TimeMeasure.New($"Load.PendingRequirees on '{this}'"))
			{
				var requirees = ModLoader.GetRequirees(this);

				if (requirees != null)
				{
					foreach (var requiree in requirees)
					{
						Logger.Warn($" [{Name}] Loading '{Path.GetFileNameWithoutExtension(requiree)}' to parent's request: '{ToString()}'");
						Community.Runtime.ScriptProcessor.Prepare(requiree);
					}

					ModLoader.ClearPendingRequirees(this);
				}
			}

			Load();
		}
		public virtual void Load()
		{

		}
		public virtual void IUnload()
		{
			try
			{
				using (TimeMeasure.New($"IUnload.UnprocessHooks on '{this}'"))
				{
					if (Hooks != null)
					{
						foreach (var hook in Hooks)
						{
							Community.Runtime.HookManager.Unsubscribe(HookStringPool.GetOrAdd(hook), FileName);
						}
						Carbon.Logger.Debug(Name, $"Unprocessed hooks");
					}
				}
			}
			catch (Exception ex)
			{
				Logger.Error($"Failed calling Plugin.IUnload.UnprocessHooks on {this}", ex);
			}

			try
			{
				using (TimeMeasure.New($"IUnload.Disposal on '{this}'"))
				{
					IgnoredHooks?.Clear();
					HookCache?.Clear();
					Hooks?.Clear();
					HookMethods?.Clear();
					PluginReferences?.Clear();
					HookMethodAttributeCache?.Clear();

					IgnoredHooks = null;
					HookCache = null;
					Hooks = null;
					HookMethods = null;
					PluginReferences = null;
					HookMethodAttributeCache = null;
				}
			}
			catch (Exception ex)
			{
				Logger.Error($"Failed calling Plugin.IUnload.Disposal on {this}", ex);
			}
		}

		internal bool InternalApplyPluginReferences()
		{
			if (PluginReferences == null) return true;

			foreach (var attribute in PluginReferences)
			{
				var field = attribute.Field;
				var name = string.IsNullOrEmpty(attribute.Name) ? field.Name : attribute.Name;
				var path = Path.Combine(Defines.GetScriptFolder(), $"{name}.cs");

				var plugin = (Plugin)null;
				if (field.FieldType.Name != nameof(Plugin) &&
					field.FieldType.Name != nameof(RustPlugin) &&
					field.FieldType.Name != nameof(CarbonPlugin))
				{
					var info = field.FieldType.GetCustomAttribute<InfoAttribute>();
					if (info == null)
					{
						Carbon.Logger.Warn($"You're trying to reference a non-plugin instance: {name}[{field.FieldType.Name}]");
						continue;
					}

					plugin = Community.Runtime.CorePlugin.plugins.Find(info.Title);
				}
				else
				{
					plugin = Community.Runtime.CorePlugin.plugins.Find(name);
				}

				if (plugin != null)
				{
					var version = new VersionNumber(attribute.MinVersion);

					if (version.IsValid() && plugin.Version < version)
					{
						Logger.Warn($"Plugin '{Name} by {Author} v{Version}' references a required plugin which is outdated: {plugin.Name} by {plugin.Author} v{plugin.Version} < v{version}");
						return false;
					}
					else
					{
						field.SetValue(this, plugin);

						if (attribute.IsRequired)
						{
							ModLoader.AddPendingRequiree(plugin, this);
						}
					}
				}
				else if (attribute.IsRequired)
				{
					ModLoader.PostBatchFailedRequirees.Add(FilePath);
					ModLoader.AddPendingRequiree(path, FilePath);
					Logger.Warn($"Plugin '{Name} by {Author} v{Version}' references a required plugin which is not loaded: {name}");
					return false;
				}
			}

			return true;
		}
		internal bool IUnloadDependantPlugins()
		{
			try
			{
				using (TimeMeasure.New($"IUnload.UnloadRequirees on '{this}'"))
				{
					var mods = Pool.GetList<ModLoader.ModPackage>();
					mods.AddRange(ModLoader.LoadedPackages);
					var plugins = Pool.GetList<Plugin>();

					foreach (var mod in ModLoader.LoadedPackages)
					{
						plugins.Clear();
						plugins.AddRange(mod.Plugins);

						foreach (Plugin plugin in plugins.Where(plugin => plugin.Requires != null && plugin.Requires.Contains(this)))
						{
							switch (plugin.Processor)
							{
								case IScriptProcessor script:
									Logger.Warn($" [{Name}] Unloading '{plugin.ToString()}' because parent '{ToString()}' has been unloaded.");
									ModLoader.AddPendingRequiree(this, plugin);

									script.Get<IScriptProcessor.IScript>(plugin.FileName)?.Dispose();

									if (plugin is RustPlugin rustPlugin)
									{
										ModLoader.UninitializePlugin(rustPlugin);
									}

									break;
							}
						}
					}

					Pool.FreeList(ref mods);
					Pool.FreeList(ref plugins);
				}

				return true;
			}
			catch (Exception ex)
			{
				Logger.Error($"Failed calling Plugin.IUnload.UnloadRequirees on {this}", ex);
				return false;
			}
		}

		public static void InternalApplyAllPluginReferences()
		{
			var list = Pool.GetList<RustPlugin>();

			foreach (var package in ModLoader.LoadedPackages)
			{
				foreach (var plugin in package.Plugins)
				{
					if (!plugin.InternalApplyPluginReferences())
					{
						list.Add(plugin);
					}
				}
			}

			foreach (var plugin in list)
			{
				ModLoader.UninitializePlugin(plugin);
			}

			Pool.FreeList(ref list);
		}

		public void SetProcessor(IBaseProcessor processor)
		{
			Processor = processor;
		}

		#region Calls

		public T Call<T>(string hook)
		{
			return HookCaller.CallHook<T>(this, HookStringPool.GetOrAdd(hook));
		}
		public T Call<T>(string hook, object arg1)
		{
			return HookCaller.CallHook<T>(this, HookStringPool.GetOrAdd(hook), arg1);
		}
		public T Call<T>(string hook, object arg1, object arg2)
		{
			return HookCaller.CallHook<T>(this, HookStringPool.GetOrAdd(hook), arg1, arg2);
		}
		public T Call<T>(string hook, object arg1, object arg2, object arg3)
		{
			return HookCaller.CallHook<T>(this, HookStringPool.GetOrAdd(hook), arg1, arg2, arg3);
		}
		public T Call<T>(string hook, object arg1, object arg2, object arg3, object arg4)
		{
			return HookCaller.CallHook<T>(this, HookStringPool.GetOrAdd(hook), arg1, arg2, arg3, arg4);
		}
		public T Call<T>(string hook, object arg1, object arg2, object arg3, object arg4, object arg5)
		{
			return HookCaller.CallHook<T>(this, HookStringPool.GetOrAdd(hook), arg1, arg2, arg3, arg4, arg5);
		}
		public T Call<T>(string hook, object arg1, object arg2, object arg3, object arg4, object arg5, object arg6)
		{
			return HookCaller.CallHook<T>(this, HookStringPool.GetOrAdd(hook), arg1, arg2, arg3, arg4, arg5, arg6);
		}
		public T Call<T>(string hook, object arg1, object arg2, object arg3, object arg4, object arg5, object arg6, object arg7)
		{
			return HookCaller.CallHook<T>(this, HookStringPool.GetOrAdd(hook), arg1, arg2, arg3, arg4, arg5, arg6, arg7);
		}
		public T Call<T>(string hook, object arg1, object arg2, object arg3, object arg4, object arg5, object arg6, object arg7, object arg8)
		{
			return HookCaller.CallHook<T>(this, HookStringPool.GetOrAdd(hook), arg1, arg2, arg3, arg4, arg5, arg6, arg7, arg8);
		}
		public T Call<T>(string hook, object arg1, object arg2, object arg3, object arg4, object arg5, object arg6, object arg7, object arg8, object arg9)
		{
			return HookCaller.CallHook<T>(this, HookStringPool.GetOrAdd(hook), arg1, arg2, arg3, arg4, arg5, arg6, arg7, arg8, arg9);
		}
		public T Call<T>(string hook, object arg1, object arg2, object arg3, object arg4, object arg5, object arg6, object arg7, object arg8, object arg9, object arg10)
		{
			return HookCaller.CallHook<T>(this, HookStringPool.GetOrAdd(hook), arg1, arg2, arg3, arg4, arg5, arg6, arg7, arg8, arg9, arg10);
		}
		public T Call<T>(string hook, object arg1, object arg2, object arg3, object arg4, object arg5, object arg6, object arg7, object arg8, object arg9, object arg10, object arg11)
		{
			return HookCaller.CallHook<T>(this, HookStringPool.GetOrAdd(hook), arg1, arg2, arg3, arg4, arg5, arg6, arg7, arg8, arg9, arg10, arg11);
		}
		public T Call<T>(string hook, object arg1, object arg2, object arg3, object arg4, object arg5, object arg6, object arg7, object arg8, object arg9, object arg10, object arg11, object arg12)
		{
			return HookCaller.CallHook<T>(this, HookStringPool.GetOrAdd(hook), arg1, arg2, arg3, arg4, arg5, arg6, arg7, arg8, arg9, arg10, arg11, arg12);
		}
		public T Call<T>(string hook, object[] args)
		{
			return args.Length switch
			{
				1 => HookCaller.CallHook<T>(this, HookStringPool.GetOrAdd(hook), args[0]),
				2 => HookCaller.CallHook<T>(this, HookStringPool.GetOrAdd(hook), args[0], args[1]),
				3 => HookCaller.CallHook<T>(this, HookStringPool.GetOrAdd(hook), args[0], args[1], args[2]),
				4 => HookCaller.CallHook<T>(this, HookStringPool.GetOrAdd(hook), args[0], args[1], args[2], args[3]),
				5 => HookCaller.CallHook<T>(this, HookStringPool.GetOrAdd(hook), args[0], args[1], args[2], args[3], args[4]),
				6 => HookCaller.CallHook<T>(this, HookStringPool.GetOrAdd(hook), args[0], args[1], args[2], args[3], args[4], args[5]),
				7 => HookCaller.CallHook<T>(this, HookStringPool.GetOrAdd(hook), args[0], args[1], args[2], args[3], args[4], args[5], args[6]),
				8 => HookCaller.CallHook<T>(this, HookStringPool.GetOrAdd(hook), args[0], args[1], args[2], args[3], args[4], args[5], args[6], args[7]),
				9 => HookCaller.CallHook<T>(this, HookStringPool.GetOrAdd(hook), args[0], args[1], args[2], args[3], args[4], args[5], args[6], args[7], args[8]),
				10 => HookCaller.CallHook<T>(this, HookStringPool.GetOrAdd(hook), args[0], args[1], args[2], args[3], args[4], args[5], args[6], args[7], args[8], args[9]),
				11 => HookCaller.CallHook<T>(this, HookStringPool.GetOrAdd(hook), args[0], args[1], args[2], args[3], args[4], args[5], args[6], args[7], args[8], args[9], args[10]),
				12 => HookCaller.CallHook<T>(this, HookStringPool.GetOrAdd(hook), args[0], args[1], args[2], args[3], args[4], args[5], args[6], args[7], args[8], args[9], args[10], args[11]),
				13 => HookCaller.CallHook<T>(this, HookStringPool.GetOrAdd(hook), args[0], args[1], args[2], args[3], args[4], args[5], args[6], args[7], args[8], args[9], args[10], args[11], args[13]),
				_ => HookCaller.CallHook<T>(this, HookStringPool.GetOrAdd(hook)),
			};
		}

		public object Call(string hook)
		{
			return HookCaller.CallHook(this, HookStringPool.GetOrAdd(hook));
		}
		public object Call(string hook, object arg1)
		{
			return HookCaller.CallHook(this, HookStringPool.GetOrAdd(hook), arg1);
		}
		public object Call(string hook, object arg1, object arg2)
		{
			return HookCaller.CallHook(this, HookStringPool.GetOrAdd(hook), arg1, arg2);
		}
		public object Call(string hook, object arg1, object arg2, object arg3)
		{
			return HookCaller.CallHook(this, HookStringPool.GetOrAdd(hook), arg1, arg2, arg3);
		}
		public object Call(string hook, object arg1, object arg2, object arg3, object arg4)
		{
			return HookCaller.CallHook(this, HookStringPool.GetOrAdd(hook), arg1, arg2, arg3, arg4);
		}
		public object Call(string hook, object arg1, object arg2, object arg3, object arg4, object arg5)
		{
			return HookCaller.CallHook(this, HookStringPool.GetOrAdd(hook), arg1, arg2, arg3, arg4, arg5);
		}
		public object Call(string hook, object arg1, object arg2, object arg3, object arg4, object arg5, object arg6)
		{
			return HookCaller.CallHook(this, HookStringPool.GetOrAdd(hook), arg1, arg2, arg3, arg4, arg5, arg6);
		}
		public object Call(string hook, object arg1, object arg2, object arg3, object arg4, object arg5, object arg6, object arg7)
		{
			return HookCaller.CallHook(this, HookStringPool.GetOrAdd(hook), arg1, arg2, arg3, arg4, arg5, arg6, arg7);
		}
		public object Call(string hook, object arg1, object arg2, object arg3, object arg4, object arg5, object arg6, object arg7, object arg8)
		{
			return HookCaller.CallHook(this, HookStringPool.GetOrAdd(hook), arg1, arg2, arg3, arg4, arg5, arg6, arg7, arg8);
		}
		public object Call(string hook, object arg1, object arg2, object arg3, object arg4, object arg5, object arg6, object arg7, object arg8, object arg9)
		{
			return HookCaller.CallHook(this, HookStringPool.GetOrAdd(hook), arg1, arg2, arg3, arg4, arg5, arg6, arg7, arg8, arg9);
		}
		public object Call(string hook, object arg1, object arg2, object arg3, object arg4, object arg5, object arg6, object arg7, object arg8, object arg9, object arg10)
		{
			return HookCaller.CallHook(this, HookStringPool.GetOrAdd(hook), arg1, arg2, arg3, arg4, arg5, arg6, arg7, arg8, arg9, arg10);
		}
		public object Call(string hook, object arg1, object arg2, object arg3, object arg4, object arg5, object arg6, object arg7, object arg8, object arg9, object arg10, object arg11)
		{
			return HookCaller.CallHook(this, HookStringPool.GetOrAdd(hook), arg1, arg2, arg3, arg4, arg5, arg6, arg7, arg8, arg9, arg10, arg11);
		}
		public object Call(string hook, object arg1, object arg2, object arg3, object arg4, object arg5, object arg6, object arg7, object arg8, object arg9, object arg10, object arg11, object arg12)
		{
			return HookCaller.CallHook(this, HookStringPool.GetOrAdd(hook), arg1, arg2, arg3, arg4, arg5, arg6, arg7, arg8, arg9, arg10, arg11, arg12);
		}
		public object Call(string hook, object arg1, object arg2, object arg3, object arg4, object arg5, object arg6, object arg7, object arg8, object arg9, object arg10, object arg11, object arg12, object arg13)
		{
			return HookCaller.CallHook(this, HookStringPool.GetOrAdd(hook), arg1, arg2, arg3, arg4, arg5, arg6, arg7, arg8, arg9, arg10, arg11, arg12, arg13);
		}
		public object Call(string hook, object[] args)
		{
			return args?.Length switch
			{
				1 => HookCaller.CallHook(this, HookStringPool.GetOrAdd(hook), args[0]),
				2 => HookCaller.CallHook(this, HookStringPool.GetOrAdd(hook), args[0], args[1]),
				3 => HookCaller.CallHook(this, HookStringPool.GetOrAdd(hook), args[0], args[1], args[2]),
				4 => HookCaller.CallHook(this, HookStringPool.GetOrAdd(hook), args[0], args[1], args[2], args[3]),
				5 => HookCaller.CallHook(this, HookStringPool.GetOrAdd(hook), args[0], args[1], args[2], args[3], args[4]),
				6 => HookCaller.CallHook(this, HookStringPool.GetOrAdd(hook), args[0], args[1], args[2], args[3], args[4], args[5]),
				7 => HookCaller.CallHook(this, HookStringPool.GetOrAdd(hook), args[0], args[1], args[2], args[3], args[4], args[5], args[6]),
				8 => HookCaller.CallHook(this, HookStringPool.GetOrAdd(hook), args[0], args[1], args[2], args[3], args[4], args[5], args[6], args[7]),
				9 => HookCaller.CallHook(this, HookStringPool.GetOrAdd(hook), args[0], args[1], args[2], args[3], args[4], args[5], args[6], args[7], args[8]),
				10 => HookCaller.CallHook(this, HookStringPool.GetOrAdd(hook), args[0], args[1], args[2], args[3], args[4], args[5], args[6], args[7], args[8], args[9]),
				11 => HookCaller.CallHook(this, HookStringPool.GetOrAdd(hook), args[0], args[1], args[2], args[3], args[4], args[5], args[6], args[7], args[8], args[9], args[10]),
				12 => HookCaller.CallHook(this, HookStringPool.GetOrAdd(hook), args[0], args[1], args[2], args[3], args[4], args[5], args[6], args[7], args[8], args[9], args[10], args[11]),
				13 => HookCaller.CallHook(this, HookStringPool.GetOrAdd(hook), args[0], args[1], args[2], args[3], args[4], args[5], args[6], args[7], args[8], args[9], args[10], args[11], args[12]),
				_ => HookCaller.CallHook(this, HookStringPool.GetOrAdd(hook)),
			};
		}

		public T CallHook<T>(string hook)
		{
			return HookCaller.CallHook<T>(this, HookStringPool.GetOrAdd(hook));
		}
		public T CallHook<T>(string hook, object arg1)
		{
			return HookCaller.CallHook<T>(this, HookStringPool.GetOrAdd(hook), arg1);
		}
		public T CallHook<T>(string hook, object arg1, object arg2)
		{
			return HookCaller.CallHook<T>(this, HookStringPool.GetOrAdd(hook), arg1, arg2);
		}
		public T CallHook<T>(string hook, object arg1, object arg2, object arg3)
		{
			return HookCaller.CallHook<T>(this, HookStringPool.GetOrAdd(hook), arg1, arg2, arg3);
		}
		public T CallHook<T>(string hook, object arg1, object arg2, object arg3, object arg4)
		{
			return HookCaller.CallHook<T>(this, HookStringPool.GetOrAdd(hook), arg1, arg2, arg3, arg4);
		}
		public T CallHook<T>(string hook, object arg1, object arg2, object arg3, object arg4, object arg5)
		{
			return HookCaller.CallHook<T>(this, HookStringPool.GetOrAdd(hook), arg1, arg2, arg3, arg4, arg5);
		}
		public T CallHook<T>(string hook, object arg1, object arg2, object arg3, object arg4, object arg5, object arg6)
		{
			return HookCaller.CallHook<T>(this, HookStringPool.GetOrAdd(hook), arg1, arg2, arg3, arg4, arg5, arg6);
		}
		public T CallHook<T>(string hook, object arg1, object arg2, object arg3, object arg4, object arg5, object arg6, object arg7)
		{
			return HookCaller.CallHook<T>(this, HookStringPool.GetOrAdd(hook), arg1, arg2, arg3, arg4, arg5, arg6, arg7);
		}
		public T CallHook<T>(string hook, object arg1, object arg2, object arg3, object arg4, object arg5, object arg6, object arg7, object arg8)
		{
			return HookCaller.CallHook<T>(this, HookStringPool.GetOrAdd(hook), arg1, arg2, arg3, arg4, arg5, arg6, arg7, arg8);
		}
		public T CallHook<T>(string hook, object arg1, object arg2, object arg3, object arg4, object arg5, object arg6, object arg7, object arg8, object arg9)
		{
			return HookCaller.CallHook<T>(this, HookStringPool.GetOrAdd(hook), arg1, arg2, arg3, arg4, arg5, arg6, arg7, arg8, arg9);
		}
		public T CallHook<T>(string hook, object arg1, object arg2, object arg3, object arg4, object arg5, object arg6, object arg7, object arg8, object arg9, object arg10)
		{
			return HookCaller.CallHook<T>(this, HookStringPool.GetOrAdd(hook), arg1, arg2, arg3, arg4, arg5, arg6, arg7, arg8, arg9, arg10);
		}
		public T CallHook<T>(string hook, object arg1, object arg2, object arg3, object arg4, object arg5, object arg6, object arg7, object arg8, object arg9, object arg10, object arg11)
		{
			return HookCaller.CallHook<T>(this, HookStringPool.GetOrAdd(hook), arg1, arg2, arg3, arg4, arg5, arg6, arg7, arg8, arg9, arg10, arg11);
		}
		public T CallHook<T>(string hook, object arg1, object arg2, object arg3, object arg4, object arg5, object arg6, object arg7, object arg8, object arg9, object arg10, object arg11, object arg12)
		{
			return HookCaller.CallHook<T>(this, HookStringPool.GetOrAdd(hook), arg1, arg2, arg3, arg4, arg5, arg6, arg7, arg8, arg9, arg10, arg11, arg12);
		}
		public T CallHook<T>(string hook, object arg1, object arg2, object arg3, object arg4, object arg5, object arg6, object arg7, object arg8, object arg9, object arg10, object arg11, object arg12, object arg13)
		{
			return HookCaller.CallHook<T>(this, HookStringPool.GetOrAdd(hook), arg1, arg2, arg3, arg4, arg5, arg6, arg7, arg8, arg9, arg10, arg11, arg12, arg13);
		}
		public T CallHook<T>(string hook, object[] args)
		{
			return args.Length switch
			{
				1 => HookCaller.CallHook<T>(this, HookStringPool.GetOrAdd(hook), args[0]),
				2 => HookCaller.CallHook<T>(this, HookStringPool.GetOrAdd(hook), args[0], args[1]),
				3 => HookCaller.CallHook<T>(this, HookStringPool.GetOrAdd(hook), args[0], args[1], args[2]),
				4 => HookCaller.CallHook<T>(this, HookStringPool.GetOrAdd(hook), args[0], args[1], args[2], args[3]),
				5 => HookCaller.CallHook<T>(this, HookStringPool.GetOrAdd(hook), args[0], args[1], args[2], args[3], args[4]),
				6 => HookCaller.CallHook<T>(this, HookStringPool.GetOrAdd(hook), args[0], args[1], args[2], args[3], args[4], args[5]),
				7 => HookCaller.CallHook<T>(this, HookStringPool.GetOrAdd(hook), args[0], args[1], args[2], args[3], args[4], args[5], args[6]),
				8 => HookCaller.CallHook<T>(this, HookStringPool.GetOrAdd(hook), args[0], args[1], args[2], args[3], args[4], args[5], args[6], args[7]),
				9 => HookCaller.CallHook<T>(this, HookStringPool.GetOrAdd(hook), args[0], args[1], args[2], args[3], args[4], args[5], args[6], args[7], args[8]),
				10 => HookCaller.CallHook<T>(this, HookStringPool.GetOrAdd(hook), args[0], args[1], args[2], args[3], args[4], args[5], args[6], args[7], args[8], args[9]),
				11 => HookCaller.CallHook<T>(this, HookStringPool.GetOrAdd(hook), args[0], args[1], args[2], args[3], args[4], args[5], args[6], args[7], args[8], args[9], args[10]),
				12 => HookCaller.CallHook<T>(this, HookStringPool.GetOrAdd(hook), args[0], args[1], args[2], args[3], args[4], args[5], args[6], args[7], args[8], args[9], args[10], args[11]),
				13 => HookCaller.CallHook<T>(this, HookStringPool.GetOrAdd(hook), args[0], args[1], args[2], args[3], args[4], args[5], args[6], args[7], args[8], args[9], args[10], args[11], args[13]),
				_ => HookCaller.CallHook<T>(this, HookStringPool.GetOrAdd(hook)),
			};
		}

		public object CallHook(string hook)
		{
			return HookCaller.CallHook(this, HookStringPool.GetOrAdd(hook));
		}
		public object CallHook(string hook, object arg1)
		{
			return HookCaller.CallHook(this, HookStringPool.GetOrAdd(hook), arg1);
		}
		public object CallHook(string hook, object arg1, object arg2)
		{
			return HookCaller.CallHook(this, HookStringPool.GetOrAdd(hook), arg1, arg2);
		}
		public object CallHook(string hook, object arg1, object arg2, object arg3)
		{
			return HookCaller.CallHook(this, HookStringPool.GetOrAdd(hook), arg1, arg2, arg3);
		}
		public object CallHook(string hook, object arg1, object arg2, object arg3, object arg4)
		{
			return HookCaller.CallHook(this, HookStringPool.GetOrAdd(hook), arg1, arg2, arg3, arg4);
		}
		public object CallHook(string hook, object arg1, object arg2, object arg3, object arg4, object arg5)
		{
			return HookCaller.CallHook(this, HookStringPool.GetOrAdd(hook), arg1, arg2, arg3, arg4, arg5);
		}
		public object CallHook(string hook, object arg1, object arg2, object arg3, object arg4, object arg5, object arg6)
		{
			return HookCaller.CallHook(this, HookStringPool.GetOrAdd(hook), arg1, arg2, arg3, arg4, arg5, arg6);
		}
		public object CallHook(string hook, object arg1, object arg2, object arg3, object arg4, object arg5, object arg6, object arg7)
		{
			return HookCaller.CallHook(this, HookStringPool.GetOrAdd(hook), arg1, arg2, arg3, arg4, arg5, arg6, arg7);
		}
		public object CallHook(string hook, object arg1, object arg2, object arg3, object arg4, object arg5, object arg6, object arg7, object arg8)
		{
			return HookCaller.CallHook(this, HookStringPool.GetOrAdd(hook), arg1, arg2, arg3, arg4, arg5, arg6, arg7, arg8);
		}
		public object CallHook(string hook, object arg1, object arg2, object arg3, object arg4, object arg5, object arg6, object arg7, object arg8, object arg9)
		{
			return HookCaller.CallHook(this, HookStringPool.GetOrAdd(hook), arg1, arg2, arg3, arg4, arg5, arg6, arg7, arg8, arg9);
		}
		public object CallHook(string hook, object arg1, object arg2, object arg3, object arg4, object arg5, object arg6, object arg7, object arg8, object arg9, object arg10)
		{
			return HookCaller.CallHook(this, HookStringPool.GetOrAdd(hook), arg1, arg2, arg3, arg4, arg5, arg6, arg7, arg8, arg9, arg10);
		}
		public object CallHook(string hook, object arg1, object arg2, object arg3, object arg4, object arg5, object arg6, object arg7, object arg8, object arg9, object arg10, object arg11)
		{
			return HookCaller.CallHook(this, HookStringPool.GetOrAdd(hook), arg1, arg2, arg3, arg4, arg5, arg6, arg7, arg8, arg9, arg10, arg11);
		}
		public object CallHook(string hook, object arg1, object arg2, object arg3, object arg4, object arg5, object arg6, object arg7, object arg8, object arg9, object arg10, object arg11, object arg12)
		{
			return HookCaller.CallHook(this, HookStringPool.GetOrAdd(hook), arg1, arg2, arg3, arg4, arg5, arg6, arg7, arg8, arg9, arg10, arg11, arg12);
		}
		public object CallHook(string hook, object arg1, object arg2, object arg3, object arg4, object arg5, object arg6, object arg7, object arg8, object arg9, object arg10, object arg11, object arg12, object arg13)
		{
			return HookCaller.CallHook(this, HookStringPool.GetOrAdd(hook), arg1, arg2, arg3, arg4, arg5, arg6, arg7, arg8, arg9, arg10, arg11, arg12, arg13);
		}
		public object CallHook(string hook, object[] args)
		{
			return args?.Length switch
			{
				1 => HookCaller.CallHook(this, HookStringPool.GetOrAdd(hook), args[0]),
				2 => HookCaller.CallHook(this, HookStringPool.GetOrAdd(hook), args[0], args[1]),
				3 => HookCaller.CallHook(this, HookStringPool.GetOrAdd(hook), args[0], args[1], args[2]),
				4 => HookCaller.CallHook(this, HookStringPool.GetOrAdd(hook), args[0], args[1], args[2], args[3]),
				5 => HookCaller.CallHook(this, HookStringPool.GetOrAdd(hook), args[0], args[1], args[2], args[3], args[4]),
				6 => HookCaller.CallHook(this, HookStringPool.GetOrAdd(hook), args[0], args[1], args[2], args[3], args[4], args[5]),
				7 => HookCaller.CallHook(this, HookStringPool.GetOrAdd(hook), args[0], args[1], args[2], args[3], args[4], args[5], args[6]),
				8 => HookCaller.CallHook(this, HookStringPool.GetOrAdd(hook), args[0], args[1], args[2], args[3], args[4], args[5], args[6], args[7]),
				9 => HookCaller.CallHook(this, HookStringPool.GetOrAdd(hook), args[0], args[1], args[2], args[3], args[4], args[5], args[6], args[7], args[8]),
				10 => HookCaller.CallHook(this, HookStringPool.GetOrAdd(hook), args[0], args[1], args[2], args[3], args[4], args[5], args[6], args[7], args[8], args[9]),
				11 => HookCaller.CallHook(this, HookStringPool.GetOrAdd(hook), args[0], args[1], args[2], args[3], args[4], args[5], args[6], args[7], args[8], args[9], args[10]),
				12 => HookCaller.CallHook(this, HookStringPool.GetOrAdd(hook), args[0], args[1], args[2], args[3], args[4], args[5], args[6], args[7], args[8], args[9], args[10], args[11]),
				13 => HookCaller.CallHook(this, HookStringPool.GetOrAdd(hook), args[0], args[1], args[2], args[3], args[4], args[5], args[6], args[7], args[8], args[9], args[10], args[11], args[12]),
				_ => HookCaller.CallHook(this, HookStringPool.GetOrAdd(hook)),
			};
		}

		#endregion

		#region Compatibility

		public object OnAddedToManager;
		public object OnRemovedFromManager;

		public virtual void HandleAddedToManager(PluginManager manager) { }
		public virtual void HandleRemovedFromManager(PluginManager manager) { }

		#endregion

		public void NextTick(Action callback)
		{
			var processor = Community.Runtime.CarbonProcessor;

			lock (processor.CurrentFrameLock)
			{
				processor.CurrentFrameQueue.Add(callback);
			}
		}
		public void NextFrame(Action callback)
		{
			var processor = Community.Runtime.CarbonProcessor;

			lock (processor.CurrentFrameLock)
			{
				processor.CurrentFrameQueue.Add(callback);
			}
		}
		public void QueueWorkerThread(Action<object> callback)
		{
			ThreadPool.QueueUserWorkItem(context =>
			{
				try
				{
					callback(context);
				}
				catch (Exception ex)
				{
					Carbon.Logger.Error($"Worker thread callback failed in '{Name} v{Version}'", ex);
				}
			});
		}

		public DynamicConfigFile Config { get; internal set; }

		public bool IsLoaded { get; set; }

		protected virtual void LoadConfig()
		{
			Config = new DynamicConfigFile(Path.Combine(Manager.ConfigPath, Name + ".json"));

			if (!Config.Exists(null))
			{
				LoadDefaultConfig();
				SaveConfig();
			}
			try
			{
				if (Config.Exists(null)) Config.Load(null);
			}
			catch (Exception ex)
			{
				Carbon.Logger.Error("Failed to load config file (is the config file corrupt?)", ex);
			}
		}
		protected virtual void LoadDefaultConfig()
		{
			//CallHook ( "LoadDefaultConfig" );
		}
		protected virtual void SaveConfig()
		{
			if (Config == null)
			{
				return;
			}
			try
			{
				if (Config.Count() > 0) Config.Save(null);
			}
			catch (Exception ex)
			{
				Carbon.Logger.Error("Failed to save config file (does the config have illegal objects in it?) (" + ex.Message + ")", ex);
			}
		}

		protected virtual void LoadDefaultMessages()
		{

		}

		public new string ToString()
		{
			return GetType().Name;
		}
		public virtual void Dispose()
		{
			IsLoaded = false;
		}
	}
}
