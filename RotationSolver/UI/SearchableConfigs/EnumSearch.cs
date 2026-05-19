using Dalamud.Interface.Utility;
using RotationSolver.Data;

namespace RotationSolver.UI.SearchableConfigs;

internal class EnumSearch(PropertyInfo property) : Searchable(property)
{
	protected int Value
	{
		get => Convert.ToInt32(_property.GetValue(Service.Config));
		set => _property.SetValue(Service.Config, Enum.ToObject(_property.PropertyType, value));
	}

	protected override void DrawMain()
	{
		var currentValue = Value;

		// Create a map of enum values to their descriptions
		Dictionary<int, string> enumValueToNameMap = [];
		foreach (Enum enumValue in Enum.GetValues(_property.PropertyType))
		{
			enumValueToNameMap[Convert.ToInt32(enumValue)] = enumValue.GetDescription();
		}

		string[] displayNames;
		{
			displayNames = new string[enumValueToNameMap.Count];
			var idx = 0;
			foreach (var kv in enumValueToNameMap)
			{
				displayNames[idx++] = kv.Value;
			}
		}

		var name = Name;
		var drawLabelAbove = false;

		if (displayNames.Length > 0)
		{
			// Set the width of the combo box
			var maxText = 0f;
			for (var i = 0; i < displayNames.Length; i++)
			{
				var w = ImGui.CalcTextSize(displayNames[i]).X;
				if (w > maxText)
				{
					maxText = w;
				}
			}

			var comboWidth = Math.Max(maxText + 30, DRAG_WIDTH) * Scale;

			if (!string.IsNullOrEmpty(name))
			{
				var availableWidth = ImGui.GetContentRegionAvail().X;
				var spacing = ImGui.GetStyle().ItemSpacing.X;
				var iconWidth = IsJob ? (24 * ImGuiHelpers.GlobalScale + spacing) : 0f;
				var labelWidth = ImGui.CalcTextSize(name).X;

				drawLabelAbove = comboWidth + spacing + iconWidth + labelWidth > availableWidth;
				if (drawLabelAbove)
				{
					ImGui.TextWrapped(name);
					if (ImGui.IsItemHovered())
					{
						ShowTooltip(false);
					}
				}
			}

			ImGui.SetNextItemWidth(comboWidth);

			// Find the current index of the selected value
			var currentIndex = 0;
			var tmpIdx = 0;
			var found = false;
			foreach (var kv in enumValueToNameMap)
			{
				if (kv.Key == currentValue)
				{
					currentIndex = tmpIdx;
					found = true;
					break;
				}
				tmpIdx++;
			}
			if (!found)
			{
				currentIndex = 0; // Default to first item if not found
			}

			// Cache the hash code to avoid multiple calls
			var hashCode = GetHashCode();

			// Draw the combo box
			if (ImGui.Combo($"##Config_{ID}{hashCode}", ref currentIndex, displayNames, displayNames.Length))
			{
				var i = 0;
				var selectedKey = currentValue;
				foreach (var kv in enumValueToNameMap)
				{
					if (i == currentIndex)
					{
						selectedKey = kv.Key;
						break;
					}
					i++;
				}
				Value = selectedKey;
			}
		}

		// Show tooltip if item is hovered
		if (ImGui.IsItemHovered())
		{
			ShowTooltip();
		}

		// Draw job icon if IsJob is true
		if (IsJob)
		{
			DrawJobIcon();
		}

		if (!drawLabelAbove && !string.IsNullOrEmpty(name))
		{
			ImGui.SameLine();
			ImGui.TextWrapped(name);

			// Show tooltip if item is hovered
			if (ImGui.IsItemHovered())
			{
				ShowTooltip(false);
			}
		}
	}
}