// Isolated CPU CUNet adapter; inference padding follows waifu2x-ncnn-vulkan (MIT, notice bundled).
// File protocol: packed RGB8 input/output; dimensions supplied by trusted managed caller.
#define NOMINMAX
#include <net.h>
#include <algorithm>
#include <cstdio>
#include <cstdlib>
#include <filesystem>
#include <thread>
#include <vector>

int wmain(int argc, wchar_t** argv)
{
    if (argc != 8) return 2;
    const int width = _wtoi(argv[4]), height = _wtoi(argv[5]);
    const int tile = _wtoi(argv[6]), threads = _wtoi(argv[7]);
    if (width < 1 || height < 1 || width > 15000 || height > 15000 ||
        static_cast<long long>(width) * height > 16000000 || tile < 32 || tile > 256 || threads < 1 || threads > 8) return 2;
    FILE* input = nullptr;
    if (_wfopen_s(&input, argv[1], L"rb") != 0) return 3;
    std::vector<unsigned char> source(static_cast<size_t>(width) * height * 3);
    size_t read = fread(source.data(), 1, source.size(), input);
    bool trailing = fgetc(input) != EOF; fclose(input);
    if (read != source.size() || trailing) return 3;

    ncnn::Net net;
    net.opt.use_vulkan_compute = false;
    net.opt.use_fp16_packed = false;
    net.opt.use_fp16_storage = false;
    net.opt.use_fp16_arithmetic = false;
    net.opt.use_bf16_storage = false;
    net.opt.num_threads = threads;
    const auto folder = std::filesystem::path(argv[3]);
    FILE* param = nullptr; FILE* weights = nullptr;
    if (_wfopen_s(&param, (folder / L"scale2.0x_model.param").c_str(), L"rb") != 0) return 4;
    int result = net.load_param(param); fclose(param);
    if (result != 0) return 4;
    if (_wfopen_s(&weights, (folder / L"scale2.0x_model.bin").c_str(), L"rb") != 0) return 4;
    result = net.load_model(weights); fclose(weights);
    if (result != 0) return 4;

    FILE* output = nullptr;
    if (_wfopen_s(&output, argv[2], L"w+b") != 0) return 5;
    const long long outputSize = static_cast<long long>(width) * height * 12;
    if (_fseeki64(output, outputSize - 1, SEEK_SET) != 0 || fputc(0, output) == EOF) { fclose(output); return 5; }
    const float normalize[3] = { 1.f/255, 1.f/255, 1.f/255 };
    const int cols = (width + tile - 1) / tile, rows = (height + tile - 1) / tile;
    for (int row = 0; row < rows; ++row)
    for (int col = 0; col < cols; ++col)
    {
        const int x = col * tile, y = row * tile;
        const int tw = std::min(tile, width - x), th = std::min(tile, height - y);
        const int extraX = tw % 2, extraY = th % 2;
        // CUNet uses valid convolutions: eighteen source pixels of context on each edge.
        const int left = std::max(x - 18, 0), top = std::max(y - 18, 0);
        const int right = std::min(x + tw + 18 + extraX, width), bottom = std::min(y + th + 18 + extraY, height);
        ncnn::Mat region = ncnn::Mat::from_pixels_roi(source.data(), ncnn::Mat::PIXEL_RGB, width, height,
            left, top, right - left, bottom - top);
        region.substract_mean_normalize(nullptr, normalize);
        ncnn::Mat padded;
        ncnn::copy_make_border(region, padded, std::max(18 - y, 0), std::max(y + th + 18 + extraY - height, 0),
            std::max(18 - x, 0), std::max(x + tw + 18 + extraX - width, 0), ncnn::BORDER_REPLICATE, 0.f, net.opt);
        auto extractor = net.create_extractor();
        if (extractor.input("Input1", padded) != 0) { fclose(output); return 6; }
        ncnn::Mat inferred;
        if (extractor.extract("Eltwise4", inferred) != 0 || inferred.w < tw * 2 || inferred.h < th * 2 || inferred.c != 3)
        { fclose(output); return 6; }
        std::vector<unsigned char> line(static_cast<size_t>(tw) * 6);
        for (int iy = 0; iy < th * 2; ++iy)
        {
            for (int q = 0; q < 3; ++q)
            {
                const float* pixel = inferred.channel(q).row(iy);
                for (int ix = 0; ix < tw * 2; ++ix)
                    line[static_cast<size_t>(ix) * 3 + q] = static_cast<unsigned char>(std::clamp(pixel[ix] * 255.f + .5f, 0.f, 255.f));
            }
            const long long position = (static_cast<long long>(y * 2 + iy) * width * 2 + x * 2) * 3;
            if (_fseeki64(output, position, SEEK_SET) != 0 || fwrite(line.data(), 1, line.size(), output) != line.size())
            { fclose(output); return 5; }
        }
        fprintf(stderr, "%.1f%%\n", 100.0 * (row * cols + col + 1) / (rows * cols)); fflush(stderr);
    }
    if (fclose(output) != 0) return 5;
    return 0;
}
