using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using System.Text.RegularExpressions;
using BepInEx;
using BepInEx.Configuration;
using HarmonyLib;
using JetBrains.Annotations;
using ServerSync;
using UnityEngine;

namespace FoodCustomizer;

[BepInPlugin(ModGUID, ModName, ModVersion)]
public class FoodCustomizer : BaseUnityPlugin
{
	private const string ModName = "FoodCustomizer";
	private const string ModVersion = "1.0.2";
	private const string ModGUID = "org.bepinex.plugins.foodcustomizer";

	private static FoodCustomizer mod = null!;

	private static readonly Dictionary<string, FoodConfig> foodConfigs = new();

	private static readonly ConfigSync configSync = new(ModName) { DisplayName = ModName, CurrentVersion = ModVersion, MinimumRequiredVersion = ModVersion };

	private static ConfigEntry<Toggle> serverConfigLocked = null!;
	private static ConfigEntry<Toggle> foodDecay = null!;

	private static ConfigEntry<T> config<T>(string group, string name, T value, ConfigDescription description, bool synchronizedSetting = true)
	{
		ConfigEntry<T> configEntry = mod.Config.Bind(group, name, value, description);

		SyncedConfigEntry<T> syncedConfigEntry = configSync.AddConfigEntry(configEntry);
		syncedConfigEntry.SynchronizedConfig = synchronizedSetting;

		return configEntry;
	}

	private static ConfigEntry<T> config<T>(string group, string name, T value, string description, bool synchronizedSetting = true) => config(group, name, value, new ConfigDescription(description), synchronizedSetting);

	private enum Toggle
	{
		On = 1,
		Off = 0
	}

	[PublicAPI]
	public class ConfigurationManagerAttributes
	{
		public int? Order;
		public string? Category;
		public Action<ConfigEntryBase>? CustomDrawer;
	}

	public void Awake()
	{
		mod = this;

		serverConfigLocked = config("1 - General", "Lock Configuration", Toggle.On, "If on, the configuration is locked and can be changed by server admins only.");
		configSync.AddLockingConfigEntry(serverConfigLocked);
		foodDecay = config("1 - General", "Food Decay", Toggle.On, "Can be used to disable all food decay.");

		Assembly assembly = Assembly.GetExecutingAssembly();
		Harmony harmony = new(ModGUID);
		harmony.PatchAll(assembly);
	}

	private static bool isFood(ItemDrop.ItemData.SharedData item) => item.m_itemType == ItemDrop.ItemData.ItemType.Consumable && item.m_foodStamina > 0;
	private static IEnumerable<ItemDrop.ItemData.SharedData> foodItems() => ObjectDB.instance.m_items.Select(i => i.GetComponent<ItemDrop>().m_itemData.m_shared).Where(isFood);

	[HarmonyPatch]
	private class ReadFoodConfigs
	{
		private static IEnumerable<MethodInfo> TargetMethods() => new[]
		{
			AccessTools.DeclaredMethod(typeof(ObjectDB), nameof(ObjectDB.Awake)),
			AccessTools.DeclaredMethod(typeof(ObjectDB), nameof(ObjectDB.CopyOtherDB))
		};

		[HarmonyPriority(Priority.Last)]
		private static void Postfix()
		{
			Localization english = new();
			english.SetupLanguage("English");

			Regex regex = new("['[\"\\]]");
			mod.Config.SaveOnConfigSet = false;

			foreach (ItemDrop.ItemData.SharedData food in foodItems())
			{
				string name = regex.Replace(english.Localize(food.m_name), "");
				string localizedName = regex.Replace(Localization.instance.Localize(food.m_name), "");
				ConfigurationManagerAttributes attributes = new() { Category = localizedName };

				if (!foodConfigs.ContainsKey(food.m_name))
				{
					foodConfigs.Add(food.m_name, new FoodConfig
					{
						health = config(name, "Health", food.m_food, new ConfigDescription($"Health gained from eating {localizedName}.", null, attributes)),
						stamina = config(name, "Stamina", food.m_foodStamina, new ConfigDescription($"Stamina gained from eating {localizedName}.", null, attributes)),
						healthRegen = config(name, "Health Regen", food.m_foodRegen, new ConfigDescription($"Health regen gained from eating {localizedName}.", null, attributes)),
						duration = config(name, "Duration", (int)food.m_foodBurnTime, new ConfigDescription($"Duration for {localizedName} in seconds.", null, attributes)),
						decayStart = config(name, "Decay Start Time", 0, new ConfigDescription($"Time after which the health and stamina from eating {localizedName} start to drain in seconds.", null, attributes)),
						decayValue = config(name, "Decay Value", 100, new ConfigDescription($"Percentage of the health and stamina you lose during the decay of {localizedName}.", new AcceptableValueRange<int>(0, 100), attributes)),
						eitr = config(name, "Eitr", food.m_foodEitr, new ConfigDescription($"Eitr gained from eating {localizedName}.", null, attributes))
					});
					foodConfigs[food.m_name].health.SettingChanged += UpdateFood;
					foodConfigs[food.m_name].stamina.SettingChanged += UpdateFood;
					foodConfigs[food.m_name].healthRegen.SettingChanged += UpdateFood;
					foodConfigs[food.m_name].duration.SettingChanged += UpdateFood;
					foodConfigs[food.m_name].eitr.SettingChanged += UpdateFood;
				}

				UpdateFoodValues(food);
			}

			foreach (Recipe recipe in ObjectDB.instance.m_recipes)
			{
				if (recipe.m_item is not null && isFood(recipe.m_item.m_itemData.m_shared) && foodConfigs.TryGetValue(recipe.m_item.m_itemData.m_shared.m_name, out FoodConfig cfg))
				{
					string name = regex.Replace(english.Localize(recipe.m_item.m_itemData.m_shared.m_name), "");
					string localizedName = regex.Replace(Localization.instance.Localize(recipe.m_item.m_itemData.m_shared.m_name), "");
					cfg.recipe ??= config(name, "Recipe", SerializedRequirements.fromPieceReqs(recipe.m_resources).ToString(), new ConfigDescription($"Recipe of {localizedName}.", null, new ConfigurationManagerAttributes { Category = localizedName, CustomDrawer = drawConfigTable }));

					cfg.recipe.SettingChanged += (_, _) => recipe.m_resources = new SerializedRequirements(cfg.recipe.Value).toPieceReqs(ObjectDB.instance);
					recipe.m_resources = new SerializedRequirements(cfg.recipe.Value).toPieceReqs(ObjectDB.instance);
				}
			}

			mod.Config.Save();
			mod.Config.SaveOnConfigSet = true;
		}
	}

	private class FoodConfig
	{
		public ConfigEntry<float> health = null!;
		public ConfigEntry<float> stamina = null!;
		public ConfigEntry<float> healthRegen = null!;
		public ConfigEntry<int> duration = null!;
		public ConfigEntry<int> decayStart = null!;
		public ConfigEntry<int> decayValue = null!;
		public ConfigEntry<float> eitr = null!;
		public ConfigEntry<string>? recipe;
	}

	private static readonly MethodInfo MemberwiseCloneMethod = AccessTools.DeclaredMethod(typeof(object), "MemberwiseClone");
	private static T Clone<T>(T input) where T : notnull => (T)MemberwiseCloneMethod.Invoke(input, Array.Empty<object>());

	private static void UpdateFood(object sender, EventArgs e)
	{
		Inventory[] inventories = Player.m_players.Select(p => p.GetInventory()).Concat(FindObjectsOfType<Container>().Select(c => c.GetInventory())).ToArray();

		Dictionary<string, ItemDrop.ItemData.SharedData> oldData = new();
		foreach (ItemDrop.ItemData.SharedData food in foodItems())
		{
			oldData[food.m_name] = Clone(food);
			UpdateFoodValues(food);
		}

		foreach (ItemDrop.ItemData itemdata in ItemDrop.m_instances.Select(i => i.m_itemData).Concat(inventories.SelectMany(i => i.GetAllItems())))
		{
			UpdateFoodValues(itemdata.m_shared, oldData);
		}
	}

	private static void UpdateFoodValues(ItemDrop.ItemData.SharedData food, Dictionary<string, ItemDrop.ItemData.SharedData>? originalData = null)
	{
		if (foodConfigs.TryGetValue(food.m_name, out FoodConfig config))
		{
			ItemDrop.ItemData.SharedData? original = null;
			originalData?.TryGetValue(food.m_name, out original);
			original ??= new ItemDrop.ItemData.SharedData
			{
				m_food = food.m_food,
				m_foodStamina = food.m_foodStamina,
				m_foodRegen = food.m_foodRegen,
				m_foodBurnTime = food.m_foodBurnTime,
				m_foodEitr = food.m_foodEitr
			};
			food.m_food = food.m_food == 0 || original.m_food == 0 ? config.health.Value : food.m_food / original.m_food * config.health.Value;
			food.m_foodStamina = food.m_foodStamina == 0 || original.m_foodStamina == 0 ? config.stamina.Value : food.m_foodStamina / original.m_foodStamina * config.stamina.Value;
			food.m_foodRegen = food.m_foodRegen == 0 || original.m_foodRegen == 0 ? config.healthRegen.Value : food.m_foodRegen / original.m_foodRegen * config.healthRegen.Value;
			food.m_foodBurnTime = food.m_foodBurnTime == 0 || original.m_foodBurnTime == 0 ? config.duration.Value : food.m_foodBurnTime / original.m_foodBurnTime * config.duration.Value;
			food.m_foodEitr = food.m_foodEitr == 0 || original.m_foodEitr == 0 ? config.eitr.Value : food.m_foodEitr / original.m_foodEitr * config.eitr.Value;
		}
	}

	[HarmonyPatch(typeof(Player), nameof(Player.UpdateFood))]
	private static class PatchFoodDecay
	{
		[UsedImplicitly]
		private static float LimitFoodDecay(float decay, Player.Food food)
		{
			if (foodDecay.Value == Toggle.Off)
			{
				return 1;
			}

			if (foodConfigs.TryGetValue(food.m_item.m_shared.m_name, out FoodConfig config))
			{
				// after this decay is remaining time
				decay *= food.m_item.m_shared.m_foodBurnTime;

				return Mathf.Pow(1 - (1 - Mathf.Pow(Mathf.Clamp01(decay / (config.duration.Value - config.decayStart.Value)), 0.3f)) * config.decayValue.Value / 100f, 1 / 0.3f);
			}
			return decay;
		}

		private static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructionsList)
		{
			MethodInfo clamp01 = AccessTools.DeclaredMethod(typeof(Mathf), nameof(Mathf.Clamp01));
			MethodInfo enumerator = AccessTools.DeclaredPropertyGetter(typeof(List<Player.Food>.Enumerator), nameof(List<Player.Food>.Enumerator.Current));
			List<CodeInstruction> instructions = instructionsList.ToList();
			int foodVar = 0;
			for (int i = 0; i < instructions.Count; ++i)
			{
				CodeInstruction instruction = instructions[i];
				if (instruction.Calls(enumerator))
				{
					foodVar = Array.IndexOf(new[] { OpCodes.Stloc_0, OpCodes.Stloc_1, OpCodes.Stloc_2, OpCodes.Stloc_3 }, instructions[i + 1].opcode);
					if (foodVar == -1)
					{
						foodVar = ((LocalVariableInfo)instructions[i + 1].operand).LocalIndex;
					}
				}

				yield return instruction;
				if (instruction.Calls(clamp01))
				{
					yield return new CodeInstruction(OpCodes.Ldloc, foodVar);
					yield return new CodeInstruction(OpCodes.Call, SymbolExtensions.GetMethodInfo(() => LimitFoodDecay(0, null!)));
				}
			}
		}
	}

	private static object? _configManager;

	private static object? configManager => _configManager ??= ((Func<object?>)(() =>
	{
		Assembly? bepinexConfigManager = AppDomain.CurrentDomain.GetAssemblies().FirstOrDefault(a => a.GetName().Name == "ConfigurationManager");

		Type? configManagerType = bepinexConfigManager?.GetType("ConfigurationManager.ConfigurationManager");
		return configManagerType == null ? null : BepInEx.Bootstrap.Chainloader.ManagerObject.GetComponent(configManagerType);
	}))();

	private static void drawConfigTable(ConfigEntryBase cfg)
	{
		bool locked = cfg.Description.Tags.Select(a => a.GetType().Name == "ConfigurationManagerAttributes" ? (bool?)a.GetType().GetField("ReadOnly")?.GetValue(a) : null).FirstOrDefault(v => v != null) ?? false;

		List<Requirement> newReqs = new();
		bool wasUpdated = false;

		int RightColumnWidth = (int)(configManager?.GetType().GetProperty("RightColumnWidth", BindingFlags.Instance | BindingFlags.NonPublic)!.GetGetMethod(true).Invoke(configManager, Array.Empty<object>()) ?? 130);

		GUILayout.BeginVertical();
		foreach (Requirement req in new SerializedRequirements((string)cfg.BoxedValue).Reqs)
		{
			GUILayout.BeginHorizontal();

			int amount = req.amount;
			if (int.TryParse(GUILayout.TextField(amount.ToString(), new GUIStyle(GUI.skin.textField) { fixedWidth = 40 }), out int newAmount) && newAmount != amount && !locked)
			{
				amount = newAmount;
				wasUpdated = true;
			}

			string newItemName = GUILayout.TextField(req.itemName, new GUIStyle(GUI.skin.textField) { fixedWidth = RightColumnWidth - 40 - 21 - 21 - 9 });
			string itemName = locked ? req.itemName : newItemName;
			wasUpdated = wasUpdated || itemName != req.itemName;

			if (GUILayout.Button("x", new GUIStyle(GUI.skin.button) { fixedWidth = 21 }) && !locked)
			{
				wasUpdated = true;
			}
			else
			{
				newReqs.Add(new Requirement { amount = amount, itemName = itemName });
			}

			if (GUILayout.Button("+", new GUIStyle(GUI.skin.button) { fixedWidth = 21 }) && !locked)
			{
				wasUpdated = true;
				newReqs.Add(new Requirement { amount = 1, itemName = "" });
			}

			GUILayout.EndHorizontal();
		}
		GUILayout.EndVertical();

		if (wasUpdated)
		{
			cfg.BoxedValue = new SerializedRequirements(newReqs).ToString();
		}
	}

	private struct Requirement
	{
		public string itemName;
		public int amount;
	}

	private class SerializedRequirements
	{
		public readonly List<Requirement> Reqs;

		public SerializedRequirements(List<Requirement> reqs) => Reqs = reqs;

		public SerializedRequirements(string reqs)
		{
			Reqs = reqs.Split(',').Select(r =>
			{
				string[] parts = r.Split(':');
				return new Requirement { itemName = parts[0], amount = parts.Length > 1 && int.TryParse(parts[1], out int amount) ? amount : 1 };
			}).ToList();
		}

		public override string ToString()
		{
			return string.Join(",", Reqs.Select(r => $"{r.itemName}:{r.amount}"));
		}

		private static ItemDrop? fetchByName(ObjectDB objectDB, string name)
		{
			ItemDrop? item = objectDB.GetItemPrefab(name)?.GetComponent<ItemDrop>();
			if (item == null)
			{
				Debug.LogWarning($"The required item '{name}' does not exist.");
			}
			return item;
		}

		public static SerializedRequirements fromPieceReqs(Piece.Requirement[] requirements) => new(requirements.Select(r => new Requirement { itemName = r.m_resItem.name, amount = r.m_amount }).ToList());

		public Piece.Requirement[] toPieceReqs(ObjectDB objectDB)
		{
			ItemDrop? ResItem(Requirement r) => fetchByName(objectDB, r.itemName);

			Dictionary<string, Piece.Requirement?> resources = Reqs.Where(r => r.itemName != "").ToDictionary(r => r.itemName, r => ResItem(r) is { } item ? new Piece.Requirement { m_amount = r.amount, m_resItem = item, m_amountPerLevel = 0 } : null);

			return resources.Values.Where(v => v != null).ToArray()!;
		}
	}
}
