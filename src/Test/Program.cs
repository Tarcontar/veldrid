using System;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Veldrid;
using Veldrid.Sdl2;
using Veldrid.SPIRV;
using Veldrid.StartupUtilities;
using Veldrid.Utilities;

namespace Test
{
    internal class Program
    {
        static void Main(string[] args)
        {
            // Just a dumb compute shader that fills a 3D texture with the same value from a uniform multiplied by the depth.
            string shaderText = @"
#version 430
layout(set = 0, binding = 0, r8ui) uniform uimage2D inputTexture;
layout(set = 0, binding = 1, r8ui) uniform uimage2D outputTexture;

layout(local_size_x = 16, local_size_y = 16) in;
void main()
{
    ivec2 textureCoordinate = ivec2(gl_GlobalInvocationID.xy);
    uvec4 color = imageLoad(inputTexture, textureCoordinate);

    imageStore(outputTexture, textureCoordinate, color);
}
";

            Sdl2Window _window;

            var wci = new WindowCreateInfo
            {
                X = 100,
                Y = 100,
                WindowWidth = 1280,
                WindowHeight = 720,
                WindowTitle = "efwefw",
            };
            _window = VeldridStartup.CreateWindow(ref wci);

            GraphicsDeviceOptions options = new GraphicsDeviceOptions(
                debug: false,
                swapchainDepthFormat: PixelFormat.R16_UNorm,
                syncToVerticalBlank: true,
                resourceBindingModel: ResourceBindingModel.Improved,
                preferDepthRangeZeroToOne: true,
                preferStandardClipSpaceYDirection: true);
#if DEBUG
            options.Debug = true;
#endif
            var GD = VeldridStartup.CreateGraphicsDevice(_window, options, GraphicsBackend.Direct3D11);
            var RF = new DisposeCollectorResourceFactory(GD.ResourceFactory);

            const byte expectedValue = 255;
            const uint TextureSize = 32;

            using Shader computeShader = RF.CreateFromSpirv(new ShaderDescription(
                ShaderStages.Compute,
                Encoding.ASCII.GetBytes(shaderText),
                "main"));

            using ResourceLayout computeLayout = RF.CreateResourceLayout(new ResourceLayoutDescription(
                new ResourceLayoutElementDescription("InputTexture", ResourceKind.TextureReadOnly, ShaderStages.Compute),
                new ResourceLayoutElementDescription("OutputTexture", ResourceKind.TextureReadWrite, ShaderStages.Compute)));

            using Pipeline computePipeline = RF.CreateComputePipeline(new ComputePipelineDescription(
                computeShader,
                computeLayout,
                16, 16, 1));

            using Texture inputTexture = RF.CreateTexture(TextureDescription.Texture2D(
                TextureSize,
                TextureSize,
                1,
                1,
                PixelFormat.R8_UInt,
                TextureUsage.Sampled | TextureUsage.Storage));
            inputTexture.Name = "INPUT_TEX";

            using TextureView inputTextureView = RF.CreateTextureView(inputTexture);

            byte[] data = Enumerable.Range(0, (int)(inputTexture.Width * inputTexture.Height)).Select(i => expectedValue).ToArray();

            unsafe
            {
                fixed (byte* dataPtr = &data[0])
                {
                    GD.UpdateTexture(inputTexture, (IntPtr)dataPtr, (uint)(data.Length * sizeof(byte)), 0, 0, 0, inputTexture.Width, inputTexture.Height, 1, 0, 0);
                }
            }

            using Texture outputTexture = RF.CreateTexture(TextureDescription.Texture2D(
                TextureSize,
                TextureSize,
                1,
                1,
                PixelFormat.R8_UInt,
                TextureUsage.Sampled | TextureUsage.Storage));
            
            outputTexture.Name = "OUTPUT_TEX";

            using TextureView outputTextureView = RF.CreateTextureView(outputTexture);

            using ResourceSet computeResourceSet = RF.CreateResourceSet(new ResourceSetDescription(
                computeLayout,
                inputTextureView,
                outputTextureView));

            using CommandList cl = RF.CreateCommandList();
            cl.Begin();

            // Use the compute shader to fill the texture.
            cl.SetPipeline(computePipeline);
            cl.SetComputeResourceSet(0, computeResourceSet);
            const uint GroupDivisorXY = 16;
            cl.Dispatch(TextureSize / GroupDivisorXY, TextureSize / GroupDivisorXY, 1);

            cl.End();
            GD.SubmitCommands(cl);
            GD.WaitForIdle();

           
            // Read back from our texture and make sure it has been properly filled.
            int notFilledCount = CountTexelsNotFilledAtDepth(GD, outputTexture, expectedValue, 0);

            Console.WriteLine($"fotFilledCount: {notFilledCount}");
            GD.SwapBuffers();
            GD.Dispose();
        }

        private static int CountTexelsNotFilledAtDepth<TexelType>(GraphicsDevice device, Texture texture, TexelType fillValue, uint depth)
            where TexelType : unmanaged
        {
            ResourceFactory factory = device.ResourceFactory;

            // We need to create a staging texture and copy into it.
            TextureDescription description = new TextureDescription(texture.Width, texture.Height, depth: 1,
                texture.MipLevels, texture.ArrayLayers,
                texture.Format, TextureUsage.Staging,
                texture.Type, texture.SampleCount);

            Texture staging = factory.CreateTexture(ref description);

            using CommandList cl = factory.CreateCommandList();
            cl.Begin();

            cl.CopyTexture(texture,
                srcX: 0, srcY: 0, srcZ: depth,
                srcMipLevel: 0, srcBaseArrayLayer: 0,
                staging,
                dstX: 0, dstY: 0, dstZ: 0,
                dstMipLevel: 0, dstBaseArrayLayer: 0,
                staging.Width, staging.Height,
                depth: 1, layerCount: 1);

            cl.End();
            device.SubmitCommands(cl);
            device.WaitForIdle();

            try
            {
                MappedResourceView<TexelType> mapped = device.Map<TexelType>(staging, MapMode.Read);

                int notFilledCount = 0;
                for (int y = 0; y < staging.Height; y++)
                {
                    for (int x = 0; x < staging.Width; x++)
                    {
                        TexelType actual = mapped[x, y];
                        if (!fillValue.Equals(actual))
                        {
                            notFilledCount++;
                        }
                    }
                }

                return notFilledCount;
            }
            finally
            {
                device.Unmap(staging);
            }
        }
    }
}
