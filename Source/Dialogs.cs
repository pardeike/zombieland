﻿using RimWorld;
using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using Verse;
using Verse.Sound;

namespace ZombieLand
{
	class SettingsDialog : Page
	{
		public override string PageTitle => "ZombielandGameSettings".Translate();

		public override void PreOpen()
		{
			base.PreOpen();
			Dialogs.scrollPosition = Vector2.zero;
		}

		public override void DoWindowContents(Rect inRect)
		{
			DrawPageTitle(inRect);
			var mainRect = GetMainRect(inRect, 0f, false);
			Dialogs.DoWindowContentsInternal(ref ZombieSettings.Values, mainRect);
			DoBottomButtons(inRect, null, null, null, true, true);
		}
	}

	public class Dialog_Save : Dialog_SaveFileList
	{
		public override bool ShouldDoTypeInField => true;

		public Dialog_Save()
		{
			interactButLabel = "OverwriteButton".Translate();
			bottomAreaHeight = 85f;
			if (Faction.OfPlayer.HasName)
				typingName = Faction.OfPlayer.Name;
			else
				typingName = SaveGameFilesUtility.UnusedDefaultFileName(Faction.OfPlayer.def.LabelCap);
		}

		public override void DoFileInteraction(string fileName)
		{
			Close(true);
			ZombieRemover.RemoveZombieland(fileName);
		}

		public override void PostClose()
		{
		}

		public static void Save()
		{
			// for quick debugging
			// ZombieRemover.RemoveZombieland(null);
			// return;

			Find.WindowStack.Add(Dialog_MessageBox.CreateConfirmation("ConfirmUninstallZombieland".Translate(), () =>
			{
				Find.WindowStack.currentlyDrawnWindow.Close();
				Find.WindowStack.Add(new Dialog_Save());

			}, true, null));
		}
	}

	static class Dialogs
	{
		static Color contentColor = new Color(1f, 1f, 1f, 0.7f);
		const float inset = 6f;
		public static string currentHelpItem = null;

		public static void Help(this Listing_Standard list, string helpItem, float height = 0f)
		{
			var curX = list.curX;
			var curY = list.curY;
			var rect = new Rect(curX, curY, list.ColumnWidth, height > 0f ? height : Text.LineHeight);
			if (Mouse.IsOver(rect))
				currentHelpItem = helpItem;
		}

		public static void Dialog_Label(this Listing_Standard list, string labelId, Color color, bool provideHelp = true)
		{
			var labelText = provideHelp ? labelId.SafeTranslate() : labelId;

			var anchor = Text.Anchor;
			Text.Anchor = TextAnchor.MiddleLeft;
			var textHeight = Text.CalcHeight(labelText, list.ColumnWidth - 3f - inset) + 2 * 3f;

			if (provideHelp) list.Help(labelId);

			var rect = list.GetRect(textHeight).Rounded();
			var color2 = color;
			color2.r *= 0.25f;
			color2.g *= 0.25f;
			color2.b *= 0.25f;
			color2.a *= 0.2f;
			GUI.color = color2;
			var r = rect.ContractedBy(1f);
			r.yMax -= 2f;
			GUI.DrawTexture(r, BaseContent.WhiteTex);
			GUI.color = color;
			rect.xMin += inset;
			Widgets.Label(rect, labelText);
			GUI.color = Color.white;
			Text.Anchor = anchor;
		}

		public static void Dialog_Text(this Listing_Standard list, GameFont font, string textId, params object[] args)
		{
			var text = textId.SafeTranslate(args);
			var anchor = Text.Anchor;
			Text.Anchor = TextAnchor.MiddleLeft;
			var savedFont = Text.Font;
			Text.Font = font;
			var textHeight = Text.CalcHeight(text, list.ColumnWidth - 3f - inset) + 2 * 3f;
			list.Help(textId);
			var rect = list.GetRect(textHeight).Rounded();
			GUI.color = Color.white;
			rect.xMin += inset;
			Widgets.Label(rect, text);
			Text.Anchor = anchor;
			Text.Font = savedFont;
		}

		public static void Dialog_Button(this Listing_Standard list, string desc, string labelId, bool dangerous, Action action)
		{
			list.Gap(6f);

			var description = desc.SafeTranslate();
			var buttonText = labelId.SafeTranslate();
			var descriptionWidth = (list.ColumnWidth - 3 * inset) * 2 / 3;
			var buttonWidth = list.ColumnWidth - 3 * inset - descriptionWidth;
			var height = Math.Max(30f, Text.CalcHeight(description, descriptionWidth));

			list.Help(labelId, height);

			var rect = list.GetRect(height);
			var rect2 = rect;
			rect.xMin += inset;
			rect.width = descriptionWidth;
			Widgets.Label(rect, description);

			rect2.xMax -= inset;
			rect2.xMin = rect2.xMax - buttonWidth;
			rect2.yMin += (height - 30f) / 2;
			rect2.yMax -= (height - 30f) / 2;

			var color = GUI.color;
			GUI.color = dangerous ? new Color(1f, 0.3f, 0.35f) : Color.white;
			if (Widgets.ButtonText(rect2, buttonText, true, true, true)) action();
			GUI.color = color;
		}

		public static void Dialog_Checkbox(this Listing_Standard list, string labelId, ref bool forBool)
		{
			list.Gap(2f);

			var label = labelId.SafeTranslate();
			var indent = 24 + "_".GetWidthCached();
			var height = Math.Max(Text.LineHeight, Text.CalcHeight(label, list.ColumnWidth - indent));

			list.Help(labelId, height);

			var rect = list.GetRect(height);
			rect.xMin += inset;

			var oldValue = forBool;
			var butRect = rect;
			butRect.xMin += 24f;
			if (Widgets.ButtonInvisible(butRect, false))
				forBool = !forBool;
			if (forBool != oldValue)
			{
				if (forBool)
					SoundDefOf.Checkbox_TurnedOn.PlayOneShotOnCamera(null);
				else
					SoundDefOf.Checkbox_TurnedOff.PlayOneShotOnCamera(null);
			}

			Widgets.Checkbox(new Vector2(rect.x, rect.y - 1f), ref forBool);

			var curX = list.curX;
			list.curX = curX + indent;

			var anchor = Text.Anchor;
			Text.Anchor = TextAnchor.UpperLeft;
			rect.xMin += indent;
			var color = GUI.color;
			GUI.color = contentColor;
			Widgets.Label(rect, label);
			GUI.color = color;
			Text.Anchor = anchor;

			list.curX = curX;
		}

		public static bool Dialog_RadioButton(this Listing_Standard list, bool active, string labelId)
		{
			var label = labelId.SafeTranslate();
			var indent = 24 + "_".GetWidthCached();
			var height = Math.Max(Text.LineHeight, Text.CalcHeight(label, list.ColumnWidth - indent));

			list.Help(labelId, height);

			var rect = list.GetRect(height);
			rect.xMin += inset;
			var line = new Rect(rect);
			var result = Widgets.RadioButton(line.xMin, line.yMin, active);

			var curX = list.curX;
			list.curX = curX + indent;

			var anchor = Text.Anchor;
			Text.Anchor = TextAnchor.UpperLeft;
			line.xMin += indent;
			var color = GUI.color;
			GUI.color = contentColor;
			Widgets.Label(line, label);
			GUI.color = color;
			Text.Anchor = anchor;

			list.curX = curX;

			result |= Widgets.ButtonInvisible(rect, false);
			if (result && !active)
				SoundDefOf.Click.PlayOneShotOnCamera(null);

			return result;
		}

		public static void Dialog_Enum<T>(this Listing_Standard list, string desc, ref T forEnum)
		{
			list.Dialog_Label(desc, Color.yellow);

			var type = forEnum.GetType();
			var choices = Enum.GetValues(type);
			foreach (var choice in choices)
			{
				list.Gap(2f);
				var label = type.Name + "_" + choice.ToString();
				if (list.Dialog_RadioButton(forEnum.Equals(choice), label))
					forEnum = (T)choice;
			}
		}

		public static void Dialog_List<T>(this Listing_Standard list, string labelId, T value, Action<T> updateValue, List<T> choices, T defaultValue) where T : IComparable<T>
		{
			var labelText = labelId.SafeTranslate();
			var valueText = choices.Contains(value) ? value.ToString() : defaultValue.ToString();

			var extraSpace = "_".GetWidthCached();
			var descLength = labelText.GetWidthCached() + extraSpace;
			var valueLength = valueText.GetWidthCached();

			list.Help(labelId, Text.LineHeight);

			var rectLine = list.GetRect(Text.LineHeight);
			rectLine.xMin += inset;
			rectLine.xMax -= inset;

			var rectLeft = rectLine.LeftPartPixels(descLength).Rounded();
			var rectRight = rectLine.RightPartPixels(valueLength).Rounded();

			var color = GUI.color;
			var anchor = Text.Anchor;
			GUI.color = contentColor;
			Text.Anchor = TextAnchor.MiddleLeft;
			Widgets.Label(rectLeft, labelText);
			GUI.color = Color.white;
			Widgets.Label(rectRight, valueText);
			Text.Anchor = anchor;
			GUI.color = color;

			if (Event.current.type == EventType.MouseDown && Mouse.IsOver(rectLine))
			{
				var found = false;
				var options = choices.Select(choice =>
				{
					var matches = choice.Equals(value);
					found |= matches;
					return new FloatMenuOption($"{choice}{(choice.Equals(value) ? " ✓" : "")}", () => updateValue(choice));
				}).ToList();
				options.Insert(0, new FloatMenuOption($"{defaultValue}{(found ? "" : " ✓")}", () => updateValue(default)));
				Find.WindowStack.Add(new FloatMenu(options));
			}
		}

		public static void Dialog_Integer(this Listing_Standard list, string labelId, string unit, int min, int max, ref int value)
		{
			list.Gap(6f);

			var unitString = unit.SafeTranslate();
			var extraSpace = "_".GetWidthCached();
			var descLength = labelId.Translate().GetWidthCached() + extraSpace;
			var unitLength = (unit == null) ? 0 : unitString.GetWidthCached() + extraSpace;

			list.Help(labelId, Text.LineHeight);

			var rectLine = list.GetRect(Text.LineHeight);
			rectLine.xMin += inset;
			rectLine.xMax -= inset;

			var rectLeft = rectLine.LeftPartPixels(descLength).Rounded();
			var rectRight = rectLine.RightPartPixels(unitLength).Rounded();
			var rectMiddle = new Rect(rectLeft.xMax, rectLeft.yMin, rectRight.xMin - rectLeft.xMax, rectLeft.height);

			var color = GUI.color;
			GUI.color = contentColor;
			Widgets.Label(rectLeft, labelId.Translate());

			var alignment = Text.CurTextFieldStyle.alignment;
			Text.CurTextFieldStyle.alignment = TextAnchor.MiddleRight;
			var buffer = value.ToString();
			Widgets.TextFieldNumeric(rectMiddle, ref value, ref buffer, min, max);
			Text.CurTextFieldStyle.alignment = alignment;

			var anchor = Text.Anchor;
			Text.Anchor = TextAnchor.MiddleRight;
			Widgets.Label(rectRight, unitString);
			Text.Anchor = anchor;

			GUI.color = color;
		}

		public static void Dialog_FloatSlider(this Listing_Standard list, string labelId, string format, ref float value, float min, float max, Func<float, float> formatFunc = null)
		{
			list.Help(labelId, 32f);

			list.Gap(12f);

			var valstr = string.Format("{0:" + format + "}", formatFunc != null ? formatFunc(value) : value);

			var srect = list.GetRect(24f);
			srect.xMin += inset;
			srect.xMax -= inset;

			value = Widgets.HorizontalSlider(srect, value, min, max, false, null, labelId.SafeTranslate(), valstr, -1f);
		}

		public static void Dialog_EnumSlider<T>(this Listing_Standard list, string labelId, ref T forEnum)
		{
			list.Help(labelId, 32f);

			var type = forEnum.GetType();
			var choices = Enum.GetValues(type);
			var max = choices.Length - 1;

			list.Gap(12f);

			var srect = list.GetRect(24f);
			srect.xMin += inset;
			srect.xMax -= inset;

			var value = $"{typeof(T).Name}_{forEnum}".SafeTranslate();
			var n = (int)Widgets.HorizontalSlider(srect, Convert.ToInt32(forEnum), 0, max, false, null, labelId.SafeTranslate(), value, 1);
			forEnum = (T)Enum.ToObject(typeof(T), n);
		}

		public static void Dialog_IntSlider(this Listing_Standard list, string labelId, Func<int, string> format, ref int value, int min, int max)
		{
			list.Help(labelId, 32f);

			list.Gap(12f);

			var srect = list.GetRect(24f);
			srect.xMin += inset;
			srect.xMax -= inset;

			value = (int)(0.5f + Widgets.HorizontalSlider(srect, value, min, max, false, null, labelId.SafeTranslate(), format(value), -1f));
		}

		public static void Dialog_TimeSlider(this Listing_Standard list, string labelId, ref int value, int min, int max, Func<int, string> valueStringConverter = null, bool fullDaysOnly = false)
		{
			list.Gap(-4f);
			list.Help(labelId, 32f);

			list.Gap(12f);

			if (valueStringConverter == null) valueStringConverter = (n) => null;
			var valstr = valueStringConverter(value) ?? Tools.TranslateHoursToText(value);

			var srect = list.GetRect(24f);
			srect.xMin += inset;
			srect.xMax -= inset;

			var newValue = (double)Widgets.HorizontalSlider(srect, value, min, max, false, null, labelId.SafeTranslate(), valstr, -1f);
			if (fullDaysOnly)
				value = (int)(Math.Round(newValue / 24f, MidpointRounding.ToEven) * 24f);
			else
				value = (int)newValue;
		}

		public static void ChooseExtractArea(Listing_Standard list, SettingsGroup settings)
		{
			if (Current.Game == null) return;
			var multiMap = Find.Maps.Count > 1;
			var areas = Find.Maps
				.SelectMany(map => map.areaManager.AllAreas
				.Where(area => area.Mutable)
				.OrderBy(area => area.ListPriority)
				.Select(area => (map, area: area)))
				.Select(pair => multiMap ? $"{pair.area.Label}:{pair.map.Index + 1}" : pair.area.Label)
				.ToList();
			list.Gap(-2f);
			list.Dialog_List("ExtractZombieArea", settings.extractZombieArea, area => settings.extractZombieArea = area ?? "", areas, "Everywhere".Translate());
		}

		public static Vector2 scrollPosition = Vector2.zero;
		public static void DoWindowContentsInternal(ref SettingsGroup settings, Rect inRect)
		{
			if (settings == null) settings = new SettingsGroup();

			inRect.yMin += 15f;
			inRect.yMax -= 15f;

			var firstColumnWidth = (inRect.width - Listing.ColumnSpacing) * 3.5f / 5f;
			var secondColumnWidth = inRect.width - Listing.ColumnSpacing - firstColumnWidth;

			var outerRect = new Rect(inRect.x, inRect.y, firstColumnWidth, inRect.height);
			var innerRect = new Rect(0f, 0f, firstColumnWidth - 24f, 3000);
			Widgets.BeginScrollView(outerRect, ref scrollPosition, innerRect, true);

			currentHelpItem = null;
			var headerColor = Color.yellow;

			var list = new Listing_Standard();
			list.Begin(innerRect);

			{
				// About
				var intro = "Zombieland_Settings".SafeTranslate();
				var textHeight = Text.CalcHeight(intro, list.ColumnWidth - 3f - inset) + 2 * 3f;
				Widgets.Label(list.GetRect(textHeight).Rounded(), intro);
				list.Gap(10f);

				// When?
				list.Dialog_Enum("WhenDoZombiesSpawn", ref settings.spawnWhenType);
				list.Gap(30f);

				// How?
				list.Dialog_Enum("HowDoZombiesSpawn", ref settings.spawnHowType);
				list.Gap(30f);

				// Attack?
				list.Dialog_Enum("WhatDoZombiesAttack", ref settings.attackMode);
				list.Dialog_Checkbox("EnemiesAttackZombies", ref settings.enemiesAttackZombies);
				list.Dialog_Checkbox("AnimalsAttackZombies", ref settings.animalsAttackZombies);
				list.Gap(30f);

				// Smash?
				list.Dialog_Enum("WhatDoZombiesSmash", ref settings.smashMode);
				if (settings.smashMode != SmashMode.Nothing)
				{
					list.Dialog_Checkbox("SmashOnlyWhenAgitated", ref settings.smashOnlyWhenAgitated);
				}
				list.Gap(30f);

				// Senses
				list.Dialog_Enum("ZombieInstinctTitle", ref settings.zombieInstinct);
				list.Dialog_Checkbox("RagingZombies", ref settings.ragingZombies);
				var rageLevelNames = new string[] { "RageLevelVeryLow", "RageLevelLow", "RageLevelNormal", "RageLevelHigh", "RageLevelVeryHigh" };
				list.Gap(8f);
				if (settings.ragingZombies)
					list.Dialog_IntSlider("RageLevel", level => rageLevelNames[level - 1].Translate(), ref settings.zombieRageLevel, 1, 5);
				list.Gap(22f);

				// Health
				list.Dialog_Label("ZombieHealthTitle", headerColor);
				list.Dialog_Checkbox("DoubleTapRequired", ref settings.doubleTapRequired);
				list.Dialog_Checkbox("ZombiesDieVeryEasily", ref settings.zombiesDieVeryEasily);
				list.Gap(30f);

				// Eating
				list.Dialog_Label("ZombieEatingTitle", headerColor);
				list.Dialog_Checkbox("ZombiesEatDowned", ref settings.zombiesEatDowned);
				list.Dialog_Checkbox("ZombiesEatCorpses", ref settings.zombiesEatCorpses);
				list.Gap(30f);

				// Types
				list.Dialog_Label("SpecialZombiesTitle", headerColor);
				list.Gap(8f);
				list.Dialog_FloatSlider("SuicideBomberChance", "0%", ref settings.suicideBomberChance, 0f, 1f - settings.toxicSplasherChance - settings.tankyOperatorChance - settings.minerChance - settings.electrifierChance - settings.albinoChance - settings.darkSlimerChance);
				list.Dialog_FloatSlider("ToxicSplasherChance", "0%", ref settings.toxicSplasherChance, 0f, 1f - settings.suicideBomberChance - settings.tankyOperatorChance - settings.minerChance - settings.electrifierChance - settings.albinoChance - settings.darkSlimerChance);
				list.Dialog_FloatSlider("TankyOperatorChance", "0%", ref settings.tankyOperatorChance, 0f, 1f - settings.suicideBomberChance - settings.toxicSplasherChance - settings.minerChance - settings.electrifierChance - settings.albinoChance - settings.darkSlimerChance);
				list.Dialog_FloatSlider("MinerChance", "0%", ref settings.minerChance, 0f, 1f - settings.suicideBomberChance - settings.toxicSplasherChance - settings.tankyOperatorChance - settings.electrifierChance - settings.albinoChance - settings.darkSlimerChance);
				list.Dialog_FloatSlider("ElectrifierChance", "0%", ref settings.electrifierChance, 0f, 1f - settings.suicideBomberChance - settings.toxicSplasherChance - settings.tankyOperatorChance - settings.minerChance - settings.albinoChance - settings.darkSlimerChance);
				list.Dialog_FloatSlider("AlbinoChance", "0%", ref settings.albinoChance, 0f, 1f - settings.suicideBomberChance - settings.toxicSplasherChance - settings.tankyOperatorChance - settings.minerChance - settings.electrifierChance - settings.darkSlimerChance);
				list.Dialog_FloatSlider("DarkSlimerChance", "0%", ref settings.darkSlimerChance, 0f, 1f - settings.suicideBomberChance - settings.toxicSplasherChance - settings.tankyOperatorChance - settings.minerChance - settings.electrifierChance - settings.albinoChance);
				var normalChance = 1 - settings.suicideBomberChance - settings.toxicSplasherChance - settings.tankyOperatorChance - settings.minerChance - settings.electrifierChance - settings.albinoChance - settings.darkSlimerChance;
				list.Gap(-6f);
				list.Dialog_Text(GameFont.Tiny, "NormalZombieChance", string.Format("{0:0%}", normalChance));
				list.Gap(30f);

				// Days
				list.Dialog_Label("NewGameTitle", headerColor);
				list.Dialog_Integer("DaysBeforeZombiesCome", null, 0, 100, ref settings.daysBeforeZombiesCome);
				list.Gap(34f);

				// Total
				list.Dialog_Label("ZombiesOnTheMap", headerColor);
				list.Dialog_Integer("MaximumNumberOfZombies", "Zombies", 0, 5000, ref settings.maximumNumberOfZombies);
				list.Gap(8f);
				list.Dialog_FloatSlider("ColonyMultiplier", "0.0x", ref settings.colonyMultiplier, 1f, 10f);
				list.Dialog_Checkbox("UseDynamicThreatLevel", ref settings.useDynamicThreatLevel);
				if (settings.useDynamicThreatLevel)
				{
					list.Gap(8f);
					list.Dialog_FloatSlider("DynamicThreatSmoothness", "0%", ref settings.dynamicThreatSmoothness, 1f, 5f, f => (f - 1f) / 4f);
					list.Gap(-4f);
					list.Dialog_FloatSlider("DynamicThreatStretch", "0%", ref settings.dynamicThreatStretch, 10f, 30f, f => (f - 10f) / 20f);
					list.Gap(-6f);
					list.Dialog_Checkbox("ZombiesDieOnZeroThreat", ref settings.zombiesDieOnZeroThreat);
				}
				list.Gap(28f);

				// Events
				list.Dialog_Label("ZombieEventTitle", headerColor);
				list.Dialog_Integer("ZombiesPerColonistInEvent", null, 0, 200, ref settings.baseNumberOfZombiesinEvent);
				list.Dialog_Integer("ExtraDaysBetweenEvents", null, 0, 10000, ref settings.extraDaysBetweenEvents);
				list.Gap(34f);

				// Speed
				list.Dialog_Label("ZombieSpeedTitle", headerColor);
				list.Gap(8f);
				list.Dialog_FloatSlider("MoveSpeedIdle", "0.00x", ref settings.moveSpeedIdle, 0.01f, 2f);
				list.Dialog_FloatSlider("MoveSpeedTracking", "0.00x", ref settings.moveSpeedTracking, 0.05f, 3f);
				list.Gap(24f);

				// Damage
				list.Dialog_Label("ZombieDamageTitle", headerColor);
				list.Gap(8f);
				list.Dialog_FloatSlider("ZombieDamageFactor", "0.0x", ref settings.damageFactor, 0.1f, 4f);
				list.Gap(-4f);
				list.Dialog_Checkbox("ZombiesCauseManhunting", ref settings.zombiesCauseManhuntingResponse);
				list.Gap(36f);

				// Tweaks
				list.Dialog_Label("ZombieGameTweaks", headerColor);
				list.Gap(8f);
				list.Dialog_FloatSlider("ReduceTurretConsumption", "0%", ref settings.reducedTurretConsumption, 0f, 1f);
				list.Gap(28f);

				// Infections
				list.Dialog_Label("ZombieInfection", headerColor);
				list.Gap(8f);
				list.Dialog_FloatSlider("ZombieBiteInfectionChance", "0%", ref settings.zombieBiteInfectionChance, 0f, 1f);
				list.Dialog_TimeSlider("ZombieBiteInfectionUnknown", ref settings.hoursInfectionIsUnknown, 0, 48);
				list.Dialog_TimeSlider("ZombieBiteInfectionTreatable", ref settings.hoursInfectionIsTreatable, 0, 6 * 24);
				list.Dialog_TimeSlider("ZombieBiteInfectionPersists", ref settings.hoursInfectionPersists, 0, 30 * 24, null, true);
				list.Gap(-4f);
				list.Dialog_Checkbox("AnyTreatmentStopsInfection", ref settings.anyTreatmentStopsInfection);
				list.Gap(22f);
				static string hoursTranslator(int n) => n == -1 ? "Off".Translate() : (n == 0 ? "Immediately".Translate() : null);
				list.Dialog_TimeSlider("HoursAfterDeathToBecomeZombie", ref settings.hoursAfterDeathToBecomeZombie, -1, 24, hoursTranslator, false);
				list.Gap(30f);

				// Serum
				list.Dialog_Label("ZombieSerum", headerColor);
				list.Gap(8f);
				list.Dialog_IntSlider("CorpsesExtractAmount", amount => (amount == 0 ? "Off".TranslateSimple() : "" + amount), ref settings.corpsesExtractAmount, 0, 10);
				list.Dialog_TimeSlider("CorpsesDaysToDessicated", ref settings.corpsesHoursToDessicated, 1, 120);
				ChooseExtractArea(list, settings);
				list.Gap(28f);

				// Miscellaneous
				list.Dialog_Label("ZombieMiscTitle", headerColor);
				list.Dialog_Checkbox("UseCustomTextures", ref settings.useCustomTextures);
				list.Dialog_Checkbox("ReplaceTwinkie", ref settings.replaceTwinkie);
				list.Dialog_Checkbox("PlayCreepyAmbientSound", ref settings.playCreepyAmbientSound);
				list.Dialog_Checkbox("BetterZombieAvoidance", ref settings.betterZombieAvoidance);
				list.Dialog_Checkbox("ZombiesDropBlood", ref settings.zombiesDropBlood);
				list.Dialog_Checkbox("ZombiesBurnLonger", ref settings.zombiesBurnLonger);
				list.Gap(30f);

				// Actions
				list.Dialog_Label("ZombieActionsTitle", headerColor);
				list.Gap(8f);
				list.Dialog_Button("ZombieSettingsReset", "Reset", false, settings.Reset);

				var inGame = Current.Game != null && Current.ProgramState == ProgramState.Playing;
				if (inGame) list.Dialog_Button("UninstallZombieland", "UninstallButton", true, Dialog_Save.Save);
			}

			list.End();
			Widgets.EndScrollView();

			if (currentHelpItem != null)
			{
				outerRect.x += firstColumnWidth + Listing.ColumnSpacing;
				outerRect.width = secondColumnWidth;

				list = new Listing_Standard();
				list.Begin(outerRect);

				var title = currentHelpItem.SafeTranslate().Replace(": {0}", "");
				list.Dialog_Label(title, Color.white, false);

				list.Gap(8f);

				var text = (currentHelpItem + "_Help").SafeTranslate();
				var anchor = Text.Anchor;
				Text.Anchor = TextAnchor.MiddleLeft;
				var textHeight = Text.CalcHeight(text, list.ColumnWidth - 3f - inset) + 2 * 3f;
				var rect = list.GetRect(textHeight).Rounded();
				GUI.color = Color.white;
				Widgets.Label(rect, text);
				Text.Anchor = anchor;

				list.End();
			}
		}
	}
}
