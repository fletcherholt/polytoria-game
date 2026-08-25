// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

using Godot;
using Polytoria.Client.Settings;
using Polytoria.Shared.Settings;

namespace Polytoria.Tests;

public class GraphicsAutoDetectorTest
{
	private const RenderingDevice.DeviceType Discrete = RenderingDevice.DeviceType.DiscreteGpu;
	private const RenderingDevice.DeviceType Integrated = RenderingDevice.DeviceType.IntegratedGpu;
	private const RenderingDevice.DeviceType Unknown = RenderingDevice.DeviceType.Other;

	[Theory]
	// Named low-end integrated parts are held down even on a strong CPU.
	[InlineData(4, "Intel(R) UHD Graphics 620", Integrated, GraphicsPreset.Low)]
	[InlineData(16, "AMD Radeon(TM) Vega 8 Graphics", Integrated, GraphicsPreset.Medium)]
	// Mid integrated parts land on Medium.
	[InlineData(8, "Intel(R) Iris(R) Xe Graphics", Integrated, GraphicsPreset.Medium)]
	[InlineData(8, "AMD Radeon(TM) Graphics", Integrated, GraphicsPreset.Medium)]
	// Discrete cards scale with the CPU underneath them.
	[InlineData(16, "NVIDIA GeForce RTX 4070", Discrete, GraphicsPreset.Ultra)]
	[InlineData(4, "NVIDIA GeForce RTX 3050 Laptop GPU", Discrete, GraphicsPreset.High)]
	[InlineData(16, "AMD Radeon RX 7900 XTX", Discrete, GraphicsPreset.Ultra)]
	// Apple Silicon reports as integrated but should not be treated as low-end.
	[InlineData(8, "Apple M3 (Apple9)", Integrated, GraphicsPreset.Ultra)]
	[InlineData(8, "Apple M1", Integrated, GraphicsPreset.Medium)]
	public void Score_KnownAdapters(int cores, string gpu, RenderingDevice.DeviceType type, GraphicsPreset expected)
	{
		Assert.Equal(expected, GraphicsAutoDetector.Score(cores, gpu, type, false));
	}

	[Fact]
	public void Score_UnlistedIntegratedAdapterIsNotGivenHigh()
	{
		// Vega 10 matches none of the name patterns, but the adapter type still identifies it.
		GraphicsPreset preset = GraphicsAutoDetector.Score(8, "AMD Radeon(TM) RX Vega 10 Graphics", Integrated, false);

		Assert.NotEqual(GraphicsPreset.High, preset);
		Assert.NotEqual(GraphicsPreset.Ultra, preset);
	}

	[Fact]
	public void Score_LowAndUltraAreReachableOnDesktop()
	{
		Assert.Equal(GraphicsPreset.Low, GraphicsAutoDetector.Score(4, "Intel(R) HD Graphics 4000", Integrated, false));
		Assert.Equal(GraphicsPreset.Ultra, GraphicsAutoDetector.Score(16, "NVIDIA GeForce RTX 4080", Discrete, false));
	}

	[Fact]
	public void Score_MobileBuildsNeverExceedMedium()
	{
		Assert.Equal(GraphicsPreset.Low, GraphicsAutoDetector.Score(8, "Adreno (TM) 640", Integrated, true));
		Assert.Equal(GraphicsPreset.Medium, GraphicsAutoDetector.Score(8, "Apple M2", Integrated, true));
	}

	[Fact]
	public void Score_UnknownAdapterFallsBackToCoreCount()
	{
		// gl_compatibility and headless both report Other, so the CPU is all there is to go on.
		Assert.Equal(GraphicsPreset.Medium, GraphicsAutoDetector.Score(2, "", Unknown, false));
		Assert.Equal(GraphicsPreset.High, GraphicsAutoDetector.Score(16, "", Unknown, false));
	}
}
