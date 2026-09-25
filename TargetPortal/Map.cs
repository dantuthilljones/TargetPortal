using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using BepInEx.Configuration;
using Groups;
using HarmonyLib;
using TMPro;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;
using Object = UnityEngine.Object;

namespace TargetPortal;

public static class Map
{
	public static bool Teleporting;
	private static bool PortalAllowsAllItems;
	private static readonly Dictionary<Minimap.PinData, ZDO> activePins = new();
	private static bool shouldPortalsBeVisible = false;
	private static bool[]? visibleIconTypes;
	private static bool mapPinsHidden;
	private static GameObject favoriteList = null!;
	private static int gamepadSelection = -1;
	private static Minimap.PinData? gamepadFocus;
	private static readonly List<FavoriteEntry> favoriteEntries = new();
	private static GameObject hintPanel = null!;
	private static readonly List<GameObject> hiddenVanillaUi = new();
	private static bool hintsBuiltForGamepad;

	// Vanilla panels on the large map that have no meaning while choosing a portal.
	// These are the pin type selector rows; Minimap.m_hints covers the key hints.
	private static readonly string[] vanillaMapPanels = { "IconPanel", "IconPanel2", "IconPingPanel" };

	private class FavoriteEntry
	{
		public Minimap.PinData Pin = null!;
		public TextMeshProUGUI Label = null!;
		public Color BaseColor;
	}

	[HarmonyPatch(typeof(TeleportWorldTrigger), nameof(TeleportWorldTrigger.OnTriggerEnter))]
	private class OpenMapOnPortalEnter
	{
		private static bool Prefix(TeleportWorldTrigger __instance, Collider colliderIn)
		{
			if (colliderIn.GetComponent<Player>() != Player.m_localPlayer)
			{
				return false;
			}

			if (TargetPortal.limitToVanillaPortals.Value == TargetPortal.Toggle.On && Utils.GetPrefabName(__instance.transform.parent.gameObject) is not "portal_wood" and not "portal_stone")
			{
				return true;
			}

			bool origNoMap = Game.m_noMap;
			Game.m_noMap = false;

			PortalAllowsAllItems = __instance.m_teleportWorld.m_allowAllItems;
			Teleporting = true;
			Minimap.instance.ShowPointOnMap(__instance.transform.position);

			Game.m_noMap = origNoMap;

			if (!shouldPortalsBeVisible)
			{
				AddPortalPins();
			}

			if (InventoryGui.IsVisible())
			{
				InventoryGui.instance.Hide();
			}

			if (TargetPortal.hidePinsDuringPortal.Value == TargetPortal.Toggle.On && !mapPinsHidden)
			{
				HideMapPins();
			}

			return false;
		}
	}

	private static void ToggleIconFilters(bool force = false)
	{
		if (visibleIconTypes == null)
		{
			return;
		}

		HashSet<Sprite> locationSprites = new(Minimap.instance.m_locationIcons.Select(l => l.m_icon));
		HashSet<int> visiblePins = new(Minimap.instance.m_pins.Where(p => locationSprites.Contains(p.m_icon)).Select(p => (int)p.m_type))
		{
			AddMinimapPortalIcon.pinType,
		};

		if (TargetPortal.showPlayersDuringPortal.Value == TargetPortal.Toggle.On)
		{
			visiblePins.Add((int)Minimap.PinType.Player);
		}


		for (int i = 0; i < visibleIconTypes.Length; ++i)
		{
			if (visiblePins.Contains(i))
			{
				continue;
			}

			if (visibleIconTypes[i] && (!Minimap.instance.m_visibleIconTypes[i] || force))
			{
				Minimap.instance.ToggleIconFilter((Minimap.PinType)i);
			}
		}
	}

	// Hiding the other map pins is a config option, but it is also toggleable during
	// selection, so what gets restored afterwards depends on what is hidden right now
	// rather than on the option.
	private static void HideMapPins()
	{
		if (visibleIconTypes == null)
		{
			visibleIconTypes = new bool[Minimap.instance.m_visibleIconTypes.Length];
			Array.Copy(Minimap.instance.m_visibleIconTypes, visibleIconTypes, Minimap.instance.m_visibleIconTypes.Length);
		}

		ToggleIconFilters(true);
		mapPinsHidden = true;
	}

	private static void ShowMapPins()
	{
		ToggleIconFilters();
		mapPinsHidden = false;
	}

	private static void ToggleMapPins()
	{
		if (mapPinsHidden)
		{
			ShowMapPins();
		}
		else
		{
			HideMapPins();
		}
	}

	public static void CancelTeleport()
	{
		Teleporting = false;
		gamepadSelection = -1;
		gamepadFocus = null;
		RestoreVanillaMapUi();

		if (!shouldPortalsBeVisible)
		{
			RemovePortalPins();
		}

		if (mapPinsHidden)
		{
			ShowMapPins();
		}

		visibleIconTypes = null;
	}

	delegate bool GetPortal(out Minimap.PinData? closestPin, out ZDO? portalZDO);

	private static bool HandlePortalClick(GetPortal getPortal)
	{
		if (!Teleporting)
		{
			return true;
		}

		if (TargetPortal.ignoreItemsTeleport.Value != TargetPortal.IgnoreItems.Always && (TargetPortal.ignoreItemsTeleport.Value == TargetPortal.IgnoreItems.Never || !PortalAllowsAllItems) && !Player.m_localPlayer.IsTeleportable(false))
		{
			Player.m_localPlayer.Message(MessageHud.MessageType.Center, "$msg_noteleport");
			return false;
		}

		if (!getPortal(out Minimap.PinData? closestPin, out ZDO? portalZDO))
		{
			return false;
		}

		Quaternion rotation = portalZDO!.GetRotation();

		Minimap.instance.SetMapMode(Minimap.MapMode.Small);
		CancelTeleport();

		Player.m_localPlayer.TeleportTo(closestPin!.m_pos + rotation * Vector3.forward + Vector3.up, rotation, true);
		return false;
	}

	// Where the player is currently pointing on the map. Valheim has no gamepad cursor:
	// with a gamepad the map itself is panned under a fixed crosshair at the center of
	// the screen, which is how vanilla resolves map clicks in Minimap.UpdateMap too.
	private static Vector3 CursorScreenPos() => ZInput.IsMouseActive()
		? ZInput.pointerPosition
		: new Vector3(Screen.width / 2f, Screen.height / 2f, 0f);

	private static bool GetClosestPortal(out Minimap.PinData? closestPin, out ZDO? portalZDO)
	{
		foreach (Minimap.PinData pinData in activePins.Keys)
		{
			pinData.m_save = true;
		}

		Minimap Minimap = Minimap.instance;
		closestPin = Minimap.GetClosestPin(Minimap.ScreenToWorldPoint(CursorScreenPos()), Minimap.m_removeRadius * (Minimap.m_largeZoom * 2f));

		foreach (Minimap.PinData pinData in activePins.Keys)
		{
			pinData.m_save = false;
		}

		if (closestPin is null)
		{
			portalZDO = null;
			return false;
		}

		return activePins.TryGetValue(closestPin, out portalZDO);
	}

	[HarmonyPatch(typeof(Minimap), nameof(Minimap.SetMapMode))]
	public class LeavePortalModeOnMapClose
	{
		private static void Postfix(Minimap.MapMode mode)
		{
			if (mode != Minimap.MapMode.Large)
			{
				CancelTeleport();
			}
			else if (Teleporting)
			{
				// SetMapMode re-enables every vanilla hint row, so re-apply ours. This
				// is also what puts the panel up in the first place, since entering a
				// portal reaches here via ShowPointOnMap.
				ShowPortalSelectionUi();
			}
			else
			{
				RefreshPortalIconHint();
			}
		}
	}

	[HarmonyPatch(typeof(Minimap), nameof(Minimap.Start))]
	public class AddMinimapPortalIcon
	{
		public static int pinType;

		private static void Postfix(Minimap __instance)
		{
			pinType = __instance.m_visibleIconTypes.Length;
			bool[] visibleIcons = new bool[pinType + 1];
			Array.Copy(__instance.m_visibleIconTypes, visibleIcons, pinType);
			__instance.m_visibleIconTypes = visibleIcons;

			__instance.m_icons.Add(new Minimap.SpriteData
			{
				m_name = (Minimap.PinType)pinType,
				m_icon = TargetPortal.portalIcon,
			});
		}
	}

	[HarmonyPatch(typeof(Minimap), nameof(Minimap.OnMapLeftClick))]
	private class MapLeftClick
	{
		private static bool Prefix()
		{
			if (!Teleporting)
			{
				return true;
			}
			return HandlePortalClick(GetClosestPortal);
		}
	}

	[HarmonyPatch(typeof(Minimap), nameof(Minimap.RemovePinUnderPointer))]
	private class MapRightClick
	{
		private static void Prefix()
		{
			if (!GetClosestPortal(out _, out ZDO? portalZDO))
			{
				return;
			}
			ToggleFavoritePortal(portalZDO!);
		}
	}

	private static void ToggleFavoritePortal(ZDO portalZDO)
	{
		string portalIdentifier = portalZDO.GetPosition().ToString();
		if (Player.m_localPlayer.m_customData.TryGetValue("TargetPortal Favorites", out string portals))
		{
			List<string> portalList = portals.Split('|').ToList();

			if (!portalList.Remove(portalIdentifier))
			{
				portalList.Add(portalIdentifier);
			}

			Player.m_localPlayer.m_customData["TargetPortal Favorites"] = string.Join("|", portalList);
		}
		else
		{
			Player.m_localPlayer.m_customData.Add("TargetPortal Favorites", portalIdentifier);
		}
		
		FillFavorites();
	}

	[HarmonyPatch]
	private class MapAlternativeClick
	{
		private static IEnumerable<MethodInfo> TargetMethods() => new[]
		{
			AccessTools.DeclaredMethod(typeof(Minimap), nameof(Minimap.OnMapDblClick)),
			AccessTools.DeclaredMethod(typeof(Minimap), nameof(Minimap.RemovePinUnderPointer)),
			AccessTools.DeclaredMethod(typeof(Minimap), nameof(Minimap.OnMapMiddleClick)),
		};

		private static bool Prefix()
		{
			return !Teleporting;
		}
	}

	[HarmonyPatch(typeof(Minimap), nameof(Minimap.Start))]
	private static class RefereshPortalPins
	{
		private static void Postfix(Minimap __instance)
		{
			IEnumerator Update()
			{
				yield return null;
				while (true)
				{
					if (shouldPortalsBeVisible && !Teleporting)
					{
						AddPortalPins();
					}
					yield return new WaitForSeconds(1);
				}
			}
			__instance.StartCoroutine(Update());
		}
	}

	[HarmonyPatch(typeof(Minimap), nameof(Minimap.Awake))]
	private static class AddFavoritePins
	{
		private static void Postfix(Minimap __instance)
		{
			favoriteList = new GameObject("TargetPortal Favorites")
			{
				transform =
				{
					parent = __instance.m_largeRoot.transform,
				},
			};

			RectTransform rect = favoriteList.AddComponent<RectTransform>();
			rect.anchorMin = new Vector2(0, 0.5f);
			rect.anchorMax = new Vector2(0, 0.5f);
			rect.anchoredPosition = new Vector2(15, 0);
			rect.sizeDelta = new Vector2(200, 500);
			rect.pivot = new Vector2(0, 0.5f);
			favoriteList.AddComponent<VerticalLayoutGroup>().childForceExpandHeight = false;

			hintPanel = new GameObject("TargetPortal Hints")
			{
				transform =
				{
					parent = __instance.m_largeRoot.transform,
				},
			};

			RectTransform hintRect = hintPanel.AddComponent<RectTransform>();
			hintRect.anchorMin = new Vector2(0, 0);
			hintRect.anchorMax = new Vector2(0, 0);
			hintRect.anchoredPosition = new Vector2(15, 15);
			hintRect.sizeDelta = new Vector2(300, 200);
			hintRect.pivot = new Vector2(0, 0);
			hintPanel.AddComponent<VerticalLayoutGroup>().childForceExpandHeight = false;
			hintPanel.SetActive(false);
		}
	}

	// Everything on the large map that does not apply while choosing a portal: the key
	// hints, which describe actions that mostly do nothing here and list none of the
	// controller bindings this mod adds, and the pin type selector, whose filters this
	// mod overrides for the duration anyway.
	private static IEnumerable<GameObject> VanillaSelectionUi()
	{
		foreach (GameObject hint in Minimap.instance.m_hints)
		{
			yield return hint;
		}

		foreach (string panel in vanillaMapPanels)
		{
			if (Minimap.instance.m_largeRoot.transform.Find(panel) is { } found)
			{
				yield return found.gameObject;
			}
		}
	}

	private static void ShowPortalSelectionUi()
	{
		if (!hintPanel)
		{
			return;
		}

		foreach (GameObject element in VanillaSelectionUi())
		{
			// Records what was enabled since the last call, so this stays correct when
			// SetMapMode re-enables things underneath us, and never switches on
			// something that was already off - key hints the player has disabled, or
			// the ping panel, which is only shown in some situations.
			if (element && element.activeSelf && !hiddenVanillaUi.Contains(element))
			{
				hiddenVanillaUi.Add(element);
			}
		}

		foreach (GameObject element in hiddenVanillaUi)
		{
			if (element)
			{
				element.SetActive(false);
			}
		}

		BuildHintRows();
		hintPanel.SetActive(true);
	}

	private static void RestoreVanillaMapUi()
	{
		if (!hintPanel)
		{
			return;
		}

		foreach (GameObject element in hiddenVanillaUi)
		{
			if (element)
			{
				element.SetActive(true);
			}
		}

		hiddenVanillaUi.Clear();
		hintPanel.SetActive(false);
	}

	// Valheim resolves an action name to whatever the player has bound, including the
	// correct glyph for their controller type, so nothing here hardcodes a button face.
	private static string Bound(string action) => action.Length > 0 && ZInput.instance is { } input ? input.GetBoundKeyString(action, true) : "";

	private static string BoundPair(string first, string second)
	{
		string a = Bound(first);
		string b = Bound(second);
		return a.Length > 0 && b.Length > 0 ? $"{a} / {b}" : a + b;
	}

	// Deliberately no row for the portal icon toggle. It controls whether portal icons
	// show on the normal map, and has no visible effect while a portal is being chosen,
	// where portal pins are always shown. Listing it here reads as a broken button.
	private static void BuildHintRows()
	{
		for (int i = 0; i < hintPanel.transform.childCount; ++i)
		{
			Object.Destroy(hintPanel.transform.GetChild(i).gameObject);
		}

		hintsBuiltForGamepad = ZInput.IsGamepadActive();

		if (hintsBuiltForGamepad)
		{
			AddHintRow(Bound(TargetPortal.gamepadTravelButton.Value), "Travel");
			AddHintRow(Bound(TargetPortal.gamepadFavoriteButton.Value), "Toggle favorite");
			AddHintRow(BoundPair(TargetPortal.gamepadCyclePrevButton.Value, TargetPortal.gamepadCycleNextButton.Value), "Cycle portals");
			AddHintRow(BoundPair(TargetPortal.gamepadFavoritePrevButton.Value, TargetPortal.gamepadFavoriteNextButton.Value), "Cycle favorites");
			AddHintRow(Bound(TargetPortal.gamepadMapPinsButton.Value), "Toggle map pins");
			AddHintRow(BoundPair("JoyMapZoomIn", "JoyMapZoomOut"), "Zoom");
			AddHintRow(Bound("JoyButtonB"), "Close map");
		}
		else
		{
			AddHintRow("Left click", "Travel");
			AddHintRow("Right click", "Toggle favorite");
			AddHintRow(TargetPortal.mapPinsToggleKey.Value.MainKey is KeyCode.None ? "" : TargetPortal.mapPinsToggleKey.Value.ToString(), "Toggle map pins");
			AddHintRow(BoundPair("MapZoomIn", "MapZoomOut"), "Zoom");
			AddHintRow(Bound("Map"), "Close map");
		}
	}

	private static void AddHintRow(string keys, string description) => CreateHintRow(hintPanel.transform, keys, description);

	private static GameObject? CreateHintRow(Transform parent, string keys, string description)
	{
		// An unbound or unresolvable action simply does not get a row.
		if (keys.Length == 0)
		{
			return null;
		}

		GameObject row = Object.Instantiate(Minimap.instance.m_largeRoot.transform.Find("KeyHints/keyboard_hints/AddPin").gameObject, parent);
		// The row being cloned is one of the vanilla hints that gets deactivated during
		// portal selection, and Instantiate copies that state, so activate it explicitly.
		row.SetActive(true);
		row.GetComponent<HorizontalLayoutGroup>().childAlignment = TextAnchor.MiddleLeft;
		row.transform.Find("keyboard_hint").gameObject.SetActive(false);

		Transform label = row.transform.Find("Label");
		label.SetAsLastSibling();
		label.GetComponent<RectTransform>().pivot = new Vector2(0, 0.5f);
		label.GetComponent<TextMeshProUGUI>().text = $"<color=yellow>{keys}</color>  {description}";
		return row;
	}

	// The portal icon toggle acts on the normal map, not on portal selection, so its
	// hint belongs among vanilla's own rows. Valheim keeps one group of rows per input
	// device under KeyHints and shows whichever matches, so putting a row in each group
	// gets the right one displayed without any device checks here. These rows sit
	// inside the container that portal selection hides, so they correctly disappear
	// while a portal is being chosen.
	private static readonly Dictionary<string, GameObject> iconHintRows = new();
	private static string builtIconHintKeys = "";

	private static void RefreshPortalIconHint()
	{
		if (Minimap.instance.m_largeRoot.transform.Find("KeyHints") is not { } keyHints)
		{
			return;
		}

		string keyboardKeys = TargetPortal.mapPortalIconKey.Value.MainKey is KeyCode.None ? "" : TargetPortal.mapPortalIconKey.Value.ToString();
		string gamepadKeys = Bound(TargetPortal.gamepadIconToggleButton.Value);
		string signature = $"{keyboardKeys}|{gamepadKeys}";

		// Rebuilt only when a binding changed, or when the rows have gone away, so
		// opening the map repeatedly does not churn through clones.
		if (signature == builtIconHintKeys && iconHintRows.Count > 0 && iconHintRows.Values.All(row => row))
		{
			return;
		}

		builtIconHintKeys = signature;
		SetPortalIconHintRow(keyHints, "keyboard_hints", keyboardKeys);
		SetPortalIconHintRow(keyHints, "gamepad_hints", gamepadKeys);
	}

	private static void SetPortalIconHintRow(Transform keyHints, string group, string keys)
	{
		if (iconHintRows.TryGetValue(group, out GameObject existing) && existing)
		{
			Object.Destroy(existing);
		}

		iconHintRows.Remove(group);

		if (keyHints.Find(group) is { } container && CreateHintRow(container, keys, "Toggle portal icons") is { } row)
		{
			iconHintRows[group] = row;
		}
	}

	private static void ClearFavorites()
	{
		favoriteEntries.Clear();

		for (int i = 0; i < favoriteList.transform.childCount; ++i)
		{
			Object.Destroy(favoriteList.transform.GetChild(i).gameObject);
		}
	}

	private static void FillFavorites()
	{
		ClearFavorites();
		
		if (Player.m_localPlayer.m_customData.TryGetValue("TargetPortal Favorites", out string portals))
		{
			Dictionary<string, Minimap.PinData> pins = activePins.ToDictionary(p => p.Value.m_position.ToString(), p => p.Key);

			List<string> portalList = portals.Split('|').ToList();

			foreach (string portal in portalList)
			{
				if (pins.TryGetValue(portal, out Minimap.PinData pin))
				{
					GameObject favoriteEntry = Object.Instantiate(Minimap.instance.m_largeRoot.transform.Find("KeyHints/keyboard_hints/AddPin").gameObject, favoriteList.transform);
					// The row being cloned is a vanilla key hint, which is deactivated
					// while choosing a portal, so activate the clone explicitly.
					favoriteEntry.SetActive(true);
					favoriteEntry.GetComponent<HorizontalLayoutGroup>().childAlignment = TextAnchor.MiddleLeft;
					Transform label = favoriteEntry.transform.Find("Label");
					label.SetAsLastSibling();
					TextMeshProUGUI labelText = label.GetComponent<TextMeshProUGUI>();
					labelText.text = pin.m_name;
					label.GetComponent<RectTransform>().pivot = new Vector2(0, 0.5f);
					Image portalIcon = favoriteEntry.transform.Find("keyboard_hint").GetComponent<Image>();
					portalIcon.sprite = pin.m_icon;
					portalIcon.gameObject.AddComponent<FavoriteClicked>().Pin = pin;

					favoriteEntries.Add(new FavoriteEntry
					{
						Pin = pin,
						Label = labelText,
						BaseColor = labelText.color,
					});
				}
			}
		}

		// The list is rebuilt whenever a portal is favorited, so restore the highlight.
		RefreshFavoriteHighlight();
	}

	// Highlights whichever favorite the gamepad currently has centered, if any. Also
	// covers the case where cycling with the bumpers happens to land on a favorite.
	private static void RefreshFavoriteHighlight()
	{
		foreach (FavoriteEntry entry in favoriteEntries)
		{
			entry.Label.color = entry.Pin == gamepadFocus ? Color.yellow : entry.BaseColor;
		}
	}

	// Centers the map on a portal by moving the offset vanilla pans with. See
	// CyclePortalSelection for why this is not a CenterMap call.
	private static void FocusPortal(Minimap.PinData pin)
	{
		gamepadFocus = pin;
		Minimap.instance.m_mapOffset = pin.m_pos - Player.m_localPlayer.transform.position;
		RefreshFavoriteHighlight();
	}

	// Steps through the favorites list. The index is derived from what is currently
	// focused rather than stored, so switching between the bumpers and this stays
	// coherent and a rebuilt list cannot leave a stale index behind.
	private static void CycleFavoriteSelection(int direction)
	{
		if (favoriteEntries.Count == 0)
		{
			return;
		}

		int current = favoriteEntries.FindIndex(e => e.Pin == gamepadFocus);
		int next = current < 0
			? direction > 0 ? 0 : favoriteEntries.Count - 1
			: ((current + direction) % favoriteEntries.Count + favoriteEntries.Count) % favoriteEntries.Count;

		FocusPortal(favoriteEntries[next].Pin);
	}

	private class FavoriteClicked : MonoBehaviour, IPointerClickHandler
	{
		public Minimap.PinData Pin = null!;

		public void OnPointerClick(PointerEventData pointerEventData)
		{
			if (pointerEventData.button == PointerEventData.InputButton.Left)
			{
				HandlePortalClick((out Minimap.PinData? pin, out ZDO? zdo) =>
				{
					pin = Pin;
					return activePins.TryGetValue(pin, out zdo);
				});
			}
			else if (pointerEventData.button == PointerEventData.InputButton.Right)
			{
				if (activePins.TryGetValue(Pin, out ZDO zdo))
				{
					ToggleFavoritePortal(zdo);
				}
			}
		}
	}

	private static bool IconTogglePossible() => TargetPortal.allowIconToggleWithoutMap.Value == TargetPortal.Toggle.On
		? Minimap.instance.m_mode != Minimap.MapMode.None
		: Minimap.instance.m_mode == Minimap.MapMode.Large;

	private static void TogglePortalPins()
	{
		if (shouldPortalsBeVisible)
		{
			RemovePortalPins();
		}
		else
		{
			AddPortalPins();
		}

		shouldPortalsBeVisible = !shouldPortalsBeVisible;
	}

	// Portals ordered by distance from the player, so cycling walks outwards from
	// wherever the player is standing rather than in ZDO order.
	private static List<Minimap.PinData> PortalsByDistance()
	{
		Vector3 origin = Player.m_localPlayer.transform.position;
		return activePins.Keys.OrderBy(p => Vector3.Distance(origin, p.m_pos)).ToList();
	}

	// Steps the selection and centers the map on it. Because the gamepad cursor is the
	// center of the screen, centering the map puts the selected portal under the
	// crosshair - so the normal "closest portal to the cursor" lookup then picks it up
	// and no separate selection state is needed at travel time.
	private static void CyclePortalSelection(int direction)
	{
		List<Minimap.PinData> portals = PortalsByDistance();
		if (portals.Count == 0)
		{
			return;
		}

		gamepadSelection = gamepadSelection < 0
			? direction > 0 ? 0 : portals.Count - 1
			: ((gamepadSelection + direction) % portals.Count + portals.Count) % portals.Count;

		// FocusPortal moves the view by the offset vanilla itself pans with, NOT by
		// calling CenterMap: UpdateMap recalculates CenterMap(player position +
		// m_mapOffset) every frame, so a direct CenterMap call is overwritten before it
		// is ever drawn. ShowPointOnMap sets the same offset but also re-enters map mode
		// and sets an input delay, which makes repeated cycling feel unresponsive.
		FocusPortal(portals[gamepadSelection]);
	}

	// Reads a gamepad button and consumes the press, so vanilla does not also act on it
	// later in the same frame - JoyButtonA would otherwise drop a map pin on top of
	// teleporting, and the bumpers would cycle the pin icon selection.
	private static bool GamepadPressed(ConfigEntry<string> button) => GamepadPressed(button.Value);

	private static bool GamepadPressed(string button)
	{
		if (!ZInput.IsGamepadActive() || button.Length == 0 || !ZInput.GetButtonDown(button))
		{
			return false;
		}

		ZInput.ResetButtonStatus(button);
		return true;
	}

	// Vanilla's pin type selector on the map, driven by these while the map is open.
	private static readonly string[] iconSelectorButtons = { "JoyDPadUp", "JoyDPadDown", "JoyDPadRight" };

	// Valheim handles gamepad map input inline in Minimap.UpdateMap rather than through
	// OnMapLeftClick/RemovePinUnderPointer, so none of the click patches above ever fire
	// on a controller. Hook UpdateMap and act before vanilla does.
	[HarmonyPatch(typeof(Minimap), nameof(Minimap.UpdateMap))]
	private static class GamepadPortalInput
	{
		private static void Prefix(bool takeInput)
		{
			if (!takeInput || !Teleporting)
			{
				return;
			}

			if (GamepadPressed(TargetPortal.gamepadCyclePrevButton))
			{
				CyclePortalSelection(-1);
			}
			if (GamepadPressed(TargetPortal.gamepadCycleNextButton))
			{
				CyclePortalSelection(1);
			}
			if (GamepadPressed(TargetPortal.gamepadFavoritePrevButton))
			{
				CycleFavoriteSelection(-1);
			}
			if (GamepadPressed(TargetPortal.gamepadFavoriteNextButton))
			{
				CycleFavoriteSelection(1);
			}
			if (GamepadPressed(TargetPortal.gamepadTravelButton))
			{
				// Travelling ends the session and clears the pins, so stop here.
				HandlePortalClick(GetClosestPortal);
				return;
			}
			if (GamepadPressed(TargetPortal.gamepadFavoriteButton) && GetClosestPortal(out _, out ZDO? portalZDO))
			{
				ToggleFavoritePortal(portalZDO!);
			}
			if (GamepadPressed(TargetPortal.gamepadMapPinsButton))
			{
				ToggleMapPins();
			}

			// Leave vanilla's pin type selector inert rather than half usable: the
			// favorites bindings take over the d-pad axis it is navigated with, and
			// toggling a filter there fights the "hide map pins" option, which restores
			// its own filter state once the selection ends. Anything the bindings above
			// already consumed is a no-op here, so this also covers those being rebound.
			foreach (string iconSelectorButton in iconSelectorButtons)
			{
				GamepadPressed(iconSelectorButton);
			}
		}
	}

	[HarmonyPatch(typeof(Minimap), nameof(Minimap.Update))]
	private static class TogglePortalIcons
	{
		private static void Prefix(Minimap __instance)
		{
			// TakeInput is checked before GamepadPressed, which consumes the press: with
			// the order reversed a blocked frame would swallow the button silently.
			// Ignored entirely while choosing a portal, where portal pins are always
			// shown. Previously the press still flipped the hidden on/off state, which
			// inverted what the button meant afterwards and could leave portal icons
			// switched on over the normal map.
			if (!Teleporting && IconTogglePossible() && Player.m_localPlayer.GetComponent<PlayerController>().TakeInput() && (TargetPortal.mapPortalIconKey.Value.IsDown() || GamepadPressed(TargetPortal.gamepadIconToggleButton)))
			{
				TogglePortalPins();
			}

			if (Teleporting && TargetPortal.mapPinsToggleKey.Value.IsDown() && Player.m_localPlayer.GetComponent<PlayerController>().TakeInput())
			{
				ToggleMapPins();
			}

			// Rebuild if the player switched between controller and mouse mid-selection.
			if (Teleporting && hintsBuiltForGamepad != ZInput.IsGamepadActive())
			{
				BuildHintRows();
			}

			if (Teleporting && TargetPortal.showPlayersDuringPortal.Value == TargetPortal.Toggle.On)
			{
				__instance.UpdatePlayerPins(Time.deltaTime);
			}
		}
	}

	private static void AddPortalPins()
	{
		bool changedPins = false;
		HashSet<Vector3> existingPins = new(activePins.Keys.Select(p => p.m_pos));

		string myId = UserInfo.GetLocalUser().UserId.ToString();
		foreach (ZDO zdo in TargetPortal.knownPortals)
		{
			TargetPortal.PortalMode mode = (TargetPortal.PortalMode)zdo.GetInt("TargetPortal PortalMode");
			string ownerString = zdo.GetString("TargetPortal PortalOwnerId");
			if (TargetPortal.allowNonPublicPortals.Value == TargetPortal.Toggle.Off || mode == TargetPortal.PortalMode.Public || (mode == TargetPortal.PortalMode.Admin && TargetPortal.configSync.IsAdmin) || ownerString == myId.Replace("Steam_", "") || (mode == TargetPortal.PortalMode.Group && API.GroupPlayers().Contains(PlayerReference.fromPlayerInfo(ZNet.instance.m_players.FirstOrDefault(p => p.m_userInfo.m_id.ToString().Replace("Steam_", "") == ownerString)))) || (mode == TargetPortal.PortalMode.Guild && Guilds.API.GetOwnGuild() is { } guild && guild.Members.ContainsKey(new Guilds.PlayerReference { id = !ownerString.Contains('_') ? "Steam_" + ownerString : ownerString, name = zdo.GetString("TargetPortal PortalOwnerName") })))
			{
				if (existingPins.Contains(zdo.m_position))
				{
					existingPins.Remove(zdo.m_position);
				}
				else
				{
					activePins.Add(Minimap.instance.AddPin(zdo.m_position, (Minimap.PinType)AddMinimapPortalIcon.pinType, zdo.GetString("tag"), false, false), zdo);
					changedPins = true;
				}
			}
		}

		List<Minimap.PinData> remove = activePins.Keys.Where(p => existingPins.Contains(p.m_pos)).ToList();
		foreach (Minimap.PinData pin in remove)
		{
			Minimap.instance.RemovePin(pin);
			activePins.Remove(pin);
			changedPins = true;
		}

		if (changedPins)
		{
			FillFavorites();
		}
	}

	private static void RemovePortalPins()
	{
		foreach (Minimap.PinData pinData in activePins.Keys)
		{
			Minimap.instance.RemovePin(pinData);
		}
		activePins.Clear();
		
		ClearFavorites();
	}
}
