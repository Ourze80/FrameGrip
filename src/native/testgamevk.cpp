// ff_testgamevk: finto gioco Vulkan per provare il layer. Colora lo schermo a FPS liberi.
// Usa vulkan-1.dll caricandola a mano, così non serve l'SDK di Vulkan per compilarlo.
// Uso: ff_testgamevk [secondi] [activate] [off] [hdr16 | hdr10] [dump]
#define WIN32_LEAN_AND_MEAN
#define VK_NO_PROTOTYPES
#define VK_USE_PLATFORM_WIN32_KHR
#include <windows.h>
#include <vulkan/vulkan.h>
#include <cmath>
#include <cstdlib>
#include <cwchar>
#include <vector>
#include <cstdio>
#include "testdump.h"

static LRESULT CALLBACK WndProc(HWND h, UINT m, WPARAM w, LPARAM l)
{
    if (m == WM_DESTROY) { PostQuitMessage(0); return 0; }
    return DefWindowProcW(h, m, w, l);
}

#define VK_FN(name) auto name = (PFN_##name)GetProcAddress(lib, #name); if (!name) return 10

int WINAPI wWinMain(HINSTANCE inst, HINSTANCE, PWSTR cmd, int)
{
    double seconds = (cmd && *cmd) ? _wtof(cmd) : 30.0;

    HMODULE lib = LoadLibraryW(L"vulkan-1.dll");
    if (!lib) return 9;
    VK_FN(vkCreateInstance); VK_FN(vkEnumeratePhysicalDevices); VK_FN(vkGetPhysicalDeviceQueueFamilyProperties);
    VK_FN(vkCreateWin32SurfaceKHR); VK_FN(vkGetPhysicalDeviceSurfaceSupportKHR); VK_FN(vkCreateDevice);
    VK_FN(vkGetDeviceQueue); VK_FN(vkGetPhysicalDeviceSurfaceCapabilitiesKHR); VK_FN(vkGetPhysicalDeviceSurfaceFormatsKHR);
    VK_FN(vkCreateSwapchainKHR); VK_FN(vkGetSwapchainImagesKHR); VK_FN(vkCreateCommandPool);
    VK_FN(vkAllocateCommandBuffers); VK_FN(vkCreateSemaphore); VK_FN(vkCreateFence); VK_FN(vkAcquireNextImageKHR);
    VK_FN(vkBeginCommandBuffer); VK_FN(vkCmdPipelineBarrier); VK_FN(vkCmdClearColorImage); VK_FN(vkEndCommandBuffer);
    VK_FN(vkQueueSubmit); VK_FN(vkQueuePresentKHR); VK_FN(vkWaitForFences); VK_FN(vkResetFences);
    VK_FN(vkResetCommandBuffer); VK_FN(vkDeviceWaitIdle);
    VK_FN(vkCreateBuffer); VK_FN(vkGetBufferMemoryRequirements); VK_FN(vkGetPhysicalDeviceMemoryProperties);
    VK_FN(vkAllocateMemory); VK_FN(vkBindBufferMemory); VK_FN(vkMapMemory); VK_FN(vkCmdCopyImageToBuffer);

    WNDCLASSEXW wc = { sizeof(wc) };
    wc.lpfnWndProc = WndProc;
    wc.hInstance = inst;
    wc.hCursor = LoadCursor(nullptr, IDC_ARROW);
    wc.lpszClassName = L"FrameGripTestGameVk";
    RegisterClassExW(&wc);
    // "off": finestra fuori dallo schermo, visibile ma senza fuoco (le prove non disturbano chi usa il PC).
    bool offscreen = cmd && wcsstr(cmd, L"off");
    HWND hwnd = CreateWindowExW(offscreen ? WS_EX_TOOLWINDOW : 0, wc.lpszClassName, L"FrameGrip Test Game (Vulkan)", WS_OVERLAPPEDWINDOW,
        offscreen ? -3000 : 280, offscreen ? 100 : 280, offscreen ? 700 : 640, offscreen ? 800 : 360, nullptr, nullptr, inst, nullptr);
    bool activate = cmd && wcsstr(cmd, L"activate");
    ShowWindow(hwnd, activate ? SW_SHOWNORMAL : SW_SHOWNOACTIVATE);
    if (activate) SetForegroundWindow(hwnd);

    // "hdr16" / "hdr10": swapchain scRGB o HDR10, che richiedono VK_EXT_swapchain_colorspace.
    bool hdr16 = cmd && wcsstr(cmd, L"hdr16"), hdr10 = cmd && wcsstr(cmd, L"hdr10");
    const char* instExt[] = { "VK_KHR_surface", "VK_KHR_win32_surface", "VK_EXT_swapchain_colorspace" };
    VkApplicationInfo app = { VK_STRUCTURE_TYPE_APPLICATION_INFO };
    app.pApplicationName = "ff_testgamevk";
    app.apiVersion = VK_API_VERSION_1_0;
    VkInstanceCreateInfo ici = { VK_STRUCTURE_TYPE_INSTANCE_CREATE_INFO };
    ici.pApplicationInfo = &app;
    ici.enabledExtensionCount = (hdr16 || hdr10) ? 3 : 2;
    ici.ppEnabledExtensionNames = instExt;
    VkInstance instance;
    if (vkCreateInstance(&ici, nullptr, &instance) != VK_SUCCESS) return 1;

    VkWin32SurfaceCreateInfoKHR sci = { VK_STRUCTURE_TYPE_WIN32_SURFACE_CREATE_INFO_KHR };
    sci.hinstance = inst;
    sci.hwnd = hwnd;
    VkSurfaceKHR surface;
    if (vkCreateWin32SurfaceKHR(instance, &sci, nullptr, &surface) != VK_SUCCESS) return 2;

    // Prima scheda con una coda che sa disegnare e presentare su questa finestra.
    uint32_t count = 0;
    vkEnumeratePhysicalDevices(instance, &count, nullptr);
    std::vector<VkPhysicalDevice> gpus(count);
    vkEnumeratePhysicalDevices(instance, &count, gpus.data());
    VkPhysicalDevice gpu = VK_NULL_HANDLE;
    uint32_t family = 0;
    for (auto g : gpus)
    {
        uint32_t n = 0;
        vkGetPhysicalDeviceQueueFamilyProperties(g, &n, nullptr);
        std::vector<VkQueueFamilyProperties> props(n);
        vkGetPhysicalDeviceQueueFamilyProperties(g, &n, props.data());
        for (uint32_t i = 0; i < n && !gpu; i++)
        {
            VkBool32 ok = VK_FALSE;
            vkGetPhysicalDeviceSurfaceSupportKHR(g, i, surface, &ok);
            if (ok && (props[i].queueFlags & VK_QUEUE_GRAPHICS_BIT)) { gpu = g; family = i; }
        }
        if (gpu) break;
    }
    if (!gpu) return 3;

    float priority = 1.0f;
    VkDeviceQueueCreateInfo qci = { VK_STRUCTURE_TYPE_DEVICE_QUEUE_CREATE_INFO };
    qci.queueFamilyIndex = family;
    qci.queueCount = 1;
    qci.pQueuePriorities = &priority;
    const char* devExt[] = { "VK_KHR_swapchain" };
    VkDeviceCreateInfo dci = { VK_STRUCTURE_TYPE_DEVICE_CREATE_INFO };
    dci.queueCreateInfoCount = 1;
    dci.pQueueCreateInfos = &qci;
    dci.enabledExtensionCount = 1;
    dci.ppEnabledExtensionNames = devExt;
    VkDevice device;
    if (vkCreateDevice(gpu, &dci, nullptr, &device) != VK_SUCCESS) return 4;
    VkQueue queue;
    vkGetDeviceQueue(device, family, 0, &queue);

    VkSurfaceCapabilitiesKHR caps;
    vkGetPhysicalDeviceSurfaceCapabilitiesKHR(gpu, surface, &caps);
    uint32_t formatCount = 0;
    vkGetPhysicalDeviceSurfaceFormatsKHR(gpu, surface, &formatCount, nullptr);
    std::vector<VkSurfaceFormatKHR> formats(formatCount);
    vkGetPhysicalDeviceSurfaceFormatsKHR(gpu, surface, &formatCount, formats.data());

    VkSwapchainCreateInfoKHR swci = { VK_STRUCTURE_TYPE_SWAPCHAIN_CREATE_INFO_KHR };
    swci.surface = surface;
    swci.minImageCount = caps.minImageCount < 2 ? 2 : caps.minImageCount;
    swci.imageFormat = formats[0].format;
    swci.imageColorSpace = formats[0].colorSpace;
    if (hdr16 || hdr10)
    {
        VkFormat wantF = hdr16 ? VK_FORMAT_R16G16B16A16_SFLOAT : VK_FORMAT_A2B10G10R10_UNORM_PACK32;
        VkColorSpaceKHR wantS = hdr16 ? VK_COLOR_SPACE_EXTENDED_SRGB_LINEAR_EXT : VK_COLOR_SPACE_HDR10_ST2084_EXT;
        bool found = false;
        for (auto& sf : formats)
            if (sf.format == wantF && sf.colorSpace == wantS) found = true;
        if (!found) return 11;   // questa scheda/schermo non offre il formato HDR richiesto
        swci.imageFormat = wantF;
        swci.imageColorSpace = wantS;
    }
    swci.imageExtent = caps.currentExtent;
    swci.imageArrayLayers = 1;
    swci.imageUsage = VK_IMAGE_USAGE_COLOR_ATTACHMENT_BIT | VK_IMAGE_USAGE_TRANSFER_DST_BIT | VK_IMAGE_USAGE_TRANSFER_SRC_BIT;
    swci.imageSharingMode = VK_SHARING_MODE_EXCLUSIVE;
    swci.preTransform = caps.currentTransform;
    swci.compositeAlpha = VK_COMPOSITE_ALPHA_OPAQUE_BIT_KHR;
    swci.presentMode = VK_PRESENT_MODE_IMMEDIATE_KHR;
    swci.clipped = VK_TRUE;
    VkSwapchainKHR swapchain;
    if (vkCreateSwapchainKHR(device, &swci, nullptr, &swapchain) != VK_SUCCESS)
    {
        swci.presentMode = VK_PRESENT_MODE_FIFO_KHR;   // sempre disponibile
        if (vkCreateSwapchainKHR(device, &swci, nullptr, &swapchain) != VK_SUCCESS) return 5;
    }
    uint32_t imageCount = 0;
    vkGetSwapchainImagesKHR(device, swapchain, &imageCount, nullptr);
    std::vector<VkImage> images(imageCount);
    vkGetSwapchainImagesKHR(device, swapchain, &imageCount, images.data());

    VkCommandPoolCreateInfo pci = { VK_STRUCTURE_TYPE_COMMAND_POOL_CREATE_INFO };
    pci.flags = VK_COMMAND_POOL_CREATE_RESET_COMMAND_BUFFER_BIT;
    pci.queueFamilyIndex = family;
    VkCommandPool pool;
    vkCreateCommandPool(device, &pci, nullptr, &pool);
    VkCommandBufferAllocateInfo cai = { VK_STRUCTURE_TYPE_COMMAND_BUFFER_ALLOCATE_INFO };
    cai.commandPool = pool;
    cai.level = VK_COMMAND_BUFFER_LEVEL_PRIMARY;
    cai.commandBufferCount = 1;
    VkCommandBuffer cb;
    vkAllocateCommandBuffers(device, &cai, &cb);

    VkSemaphoreCreateInfo semInfo = { VK_STRUCTURE_TYPE_SEMAPHORE_CREATE_INFO };
    VkSemaphore acquired, rendered;
    vkCreateSemaphore(device, &semInfo, nullptr, &acquired);
    vkCreateSemaphore(device, &semInfo, nullptr, &rendered);
    VkFenceCreateInfo fenceInfo = { VK_STRUCTURE_TYPE_FENCE_CREATE_INFO };
    VkFence fence;
    vkCreateFence(device, &fenceInfo, nullptr, &fence);

    // "dump": verso la fine si riprende un'immagine già presentata (con l'overlay disegnato dal layer) e la si
    // copia in un buffer leggibile dalla CPU, poi in %TEMP%\ff_dump_vk.bmp. Serve a controllare l'overlay
    // senza catturare lo schermo (PrintWindow non vede le finestre Vulkan).
    bool dump = cmd && wcsstr(cmd, L"dump"), dumped = false;
    uint32_t dw = swci.imageExtent.width, dh = swci.imageExtent.height;
    uint32_t bpp = hdr16 ? 8 : 4;
    VkBuffer dumpBuf = VK_NULL_HANDLE;
    VkDeviceMemory dumpMem = VK_NULL_HANDLE;
    if (dump)
    {
        VkBufferCreateInfo bci = { VK_STRUCTURE_TYPE_BUFFER_CREATE_INFO };
        bci.size = (VkDeviceSize)dw * dh * bpp;
        bci.usage = VK_BUFFER_USAGE_TRANSFER_DST_BIT;
        if (vkCreateBuffer(device, &bci, nullptr, &dumpBuf) != VK_SUCCESS) return 6;
        VkMemoryRequirements req;
        vkGetBufferMemoryRequirements(device, dumpBuf, &req);
        VkPhysicalDeviceMemoryProperties mp;
        vkGetPhysicalDeviceMemoryProperties(gpu, &mp);
        uint32_t want = VK_MEMORY_PROPERTY_HOST_VISIBLE_BIT | VK_MEMORY_PROPERTY_HOST_COHERENT_BIT, type = UINT32_MAX;
        for (uint32_t i = 0; i < mp.memoryTypeCount && type == UINT32_MAX; i++)
            if ((req.memoryTypeBits & (1u << i)) && (mp.memoryTypes[i].propertyFlags & want) == want) type = i;
        if (type == UINT32_MAX) return 7;
        VkMemoryAllocateInfo mai = { VK_STRUCTURE_TYPE_MEMORY_ALLOCATE_INFO };
        mai.allocationSize = req.size;
        mai.memoryTypeIndex = type;
        if (vkAllocateMemory(device, &mai, nullptr, &dumpMem) != VK_SUCCESS) return 8;
        vkBindBufferMemory(device, dumpBuf, dumpMem, 0);
    }
    uint32_t presented = 0;   // immagini presentate almeno una volta (prima sono UNDEFINED)

    ULONGLONG start = GetTickCount64();
    MSG msg = {};
    bool running = true;
    while (running)
    {
        while (PeekMessageW(&msg, nullptr, 0, 0, PM_REMOVE))
        {
            if (msg.message == WM_QUIT) running = false;
            TranslateMessage(&msg);
            DispatchMessageW(&msg);
        }
        double t = (GetTickCount64() - start) / 1000.0;
        if (t > seconds) break;

        uint32_t index = 0;
        if (vkAcquireNextImageKHR(device, swapchain, UINT64_MAX, acquired, VK_NULL_HANDLE, &index) < 0) break;

        vkResetCommandBuffer(cb, 0);
        VkCommandBufferBeginInfo begin = { VK_STRUCTURE_TYPE_COMMAND_BUFFER_BEGIN_INFO };
        vkBeginCommandBuffer(cb, &begin);
        VkImageSubresourceRange range = { VK_IMAGE_ASPECT_COLOR_BIT, 0, 1, 0, 1 };
        VkImageMemoryBarrier barrier = { VK_STRUCTURE_TYPE_IMAGE_MEMORY_BARRIER };
        barrier.srcQueueFamilyIndex = barrier.dstQueueFamilyIndex = VK_QUEUE_FAMILY_IGNORED;
        barrier.image = images[index];
        barrier.subresourceRange = range;
        bool grab = dump && !dumped && t > seconds - 1.5 && presented > imageCount * 4;
        if (grab)
        {
            // L'immagine ripresa è già stata presentata: contiene il fotogramma con l'overlay. Si copia così com'è.
            barrier.oldLayout = VK_IMAGE_LAYOUT_PRESENT_SRC_KHR;
            barrier.newLayout = VK_IMAGE_LAYOUT_TRANSFER_SRC_OPTIMAL;
            barrier.dstAccessMask = VK_ACCESS_TRANSFER_READ_BIT;
            vkCmdPipelineBarrier(cb, VK_PIPELINE_STAGE_TOP_OF_PIPE_BIT, VK_PIPELINE_STAGE_TRANSFER_BIT, 0, 0, nullptr, 0, nullptr, 1, &barrier);
            VkBufferImageCopy copy = {};
            copy.imageSubresource = { VK_IMAGE_ASPECT_COLOR_BIT, 0, 0, 1 };
            copy.imageExtent = { dw, dh, 1 };
            vkCmdCopyImageToBuffer(cb, images[index], VK_IMAGE_LAYOUT_TRANSFER_SRC_OPTIMAL, dumpBuf, 1, &copy);
            barrier.oldLayout = VK_IMAGE_LAYOUT_TRANSFER_SRC_OPTIMAL;
        }
        else
        {
        barrier.oldLayout = VK_IMAGE_LAYOUT_UNDEFINED;
        barrier.newLayout = VK_IMAGE_LAYOUT_TRANSFER_DST_OPTIMAL;
        barrier.dstAccessMask = VK_ACCESS_TRANSFER_WRITE_BIT;
        vkCmdPipelineBarrier(cb, VK_PIPELINE_STAGE_TOP_OF_PIPE_BIT, VK_PIPELINE_STAGE_TRANSFER_BIT, 0, 0, nullptr, 0, nullptr, 1, &barrier);
        VkClearColorValue color = { { (float)(0.5 + 0.5 * sin(t)), (float)(0.5 + 0.5 * sin(t * 1.3)), 0.2f, 1.0f } };
        if (dump) color.float32[0] = color.float32[1] = color.float32[2] = 0.0f;   // fondo nero: valori attesi esatti
        vkCmdClearColorImage(cb, images[index], VK_IMAGE_LAYOUT_TRANSFER_DST_OPTIMAL, &color, 1, &range);
        barrier.oldLayout = VK_IMAGE_LAYOUT_TRANSFER_DST_OPTIMAL;
        }
        barrier.newLayout = VK_IMAGE_LAYOUT_PRESENT_SRC_KHR;
        barrier.srcAccessMask = grab ? VK_ACCESS_TRANSFER_READ_BIT : VK_ACCESS_TRANSFER_WRITE_BIT;
        barrier.dstAccessMask = 0;
        vkCmdPipelineBarrier(cb, VK_PIPELINE_STAGE_TRANSFER_BIT, VK_PIPELINE_STAGE_BOTTOM_OF_PIPE_BIT, 0, 0, nullptr, 0, nullptr, 1, &barrier);
        vkEndCommandBuffer(cb);

        VkPipelineStageFlags wait = VK_PIPELINE_STAGE_TRANSFER_BIT;
        VkSubmitInfo submit = { VK_STRUCTURE_TYPE_SUBMIT_INFO };
        submit.waitSemaphoreCount = 1;
        submit.pWaitSemaphores = &acquired;
        submit.pWaitDstStageMask = &wait;
        submit.commandBufferCount = 1;
        submit.pCommandBuffers = &cb;
        submit.signalSemaphoreCount = 1;
        submit.pSignalSemaphores = &rendered;
        vkQueueSubmit(queue, 1, &submit, fence);

        VkPresentInfoKHR present = { VK_STRUCTURE_TYPE_PRESENT_INFO_KHR };
        present.waitSemaphoreCount = 1;
        present.pWaitSemaphores = &rendered;
        present.swapchainCount = 1;
        present.pSwapchains = &swapchain;
        present.pImageIndices = &index;
        if (vkQueuePresentKHR(queue, &present) < 0) break;
        presented++;

        vkWaitForFences(device, 1, &fence, VK_TRUE, UINT64_MAX);
        vkResetFences(device, 1, &fence);

        if (grab)
        {
            dumped = true;
            void* px = nullptr;
            vkMapMemory(device, dumpMem, 0, VK_WHOLE_SIZE, 0, &px);
            wchar_t path[MAX_PATH];
            GetTempPathW(MAX_PATH, path);
            wcscat_s(path, L"ff_dump_vk.bmp");
            // Anche nel formato per il controllo dei colori (testdump.h), con i codici DXGI equivalenti:
            // 10 = 16 bit float, 24 = 10 bit (stessa disposizione di A2B10G10R10), 87 = B8G8R8A8.
            if (px) WriteTestDump(L"vk", px, dw, dh, hdr16 ? 10u : hdr10 ? 24u : 87u, bpp, dw * bpp);
            FILE* f = nullptr;
            if (px && bpp == 4 && !hdr10 && _wfopen_s(&f, path, L"wb") == 0 && f)
            {
                // Formato atteso B8G8R8A8 (come una bitmap a 32 bit); altezza negativa = righe dall'alto.
                BITMAPFILEHEADER fh = { 0x4D42, (DWORD)(54 + dw * dh * 4), 0, 0, 54 };
                BITMAPINFOHEADER ih = { sizeof(ih), (LONG)dw, -(LONG)dh, 1, 32, BI_RGB };
                fwrite(&fh, sizeof(fh), 1, f);
                fwrite(&ih, sizeof(ih), 1, f);
                fwrite(px, 4, (size_t)dw * dh, f);
                fclose(f);
            }
        }
    }
    vkDeviceWaitIdle(device);
    return 0;
}
