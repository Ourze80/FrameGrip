// Layer Vulkan di FrameGrip: limitatore, misura dei frame e overlay disegnato dentro il gioco.
// A differenza delle altre API, Vulkan prevede un modo ufficiale per inserirsi tra gioco e driver:
// un "layer" che il sistema carica da solo in ogni gioco Vulkan. Questa stessa DLL fa da layer.
//
// Disegno dell'overlay: subito prima della presentazione si invia un piccolo comando in più, che
// dipinge l'immagine dell'overlay sopra il fotogramma finito. Il comando aspetta i semafori che il
// gioco aveva destinato alla presentazione, e la presentazione aspetta poi un semaforo nostro:
// così l'ordine "gioco -> overlay -> schermo" è garantito.
#define VK_NO_PROTOTYPES
#include "common.h"
#include <vulkan/vulkan.h>
#include <vulkan/vk_layer.h>
#include <cstring>
#include <vector>
#include <string>
#include <cwctype>
#include <cwchar>
#include <tlhelp32.h>
#include "vk_shaders.h"

// A 32 bit le funzioni Vulkan usano la convenzione stdcall, che cambia il nome esportato.
#ifdef _M_IX86
#pragma comment(linker, "/EXPORT:vkNegotiateLoaderLayerInterfaceVersion=_vkNegotiateLoaderLayerInterfaceVersion@4")
#else
#pragma comment(linker, "/EXPORT:vkNegotiateLoaderLayerInterfaceVersion")
#endif

namespace
{
    // Gli oggetti Vulkan "smistabili" iniziano con un puntatore alla tabella del loro device o
    // della loro istanza: è la chiave con cui ritrovare i nostri dati.
    inline void* Key(void* handle) { return *(void**)handle; }

    // Funzioni del device che servono per disegnare, chieste al layer successivo.
#define FF_VK_FUNCS(X) \
    X(CreateSwapchainKHR) X(DestroySwapchainKHR) X(GetSwapchainImagesKHR) X(GetDeviceQueue) X(GetDeviceQueue2) \
    X(CreateImageView) X(DestroyImageView) X(CreateRenderPass) X(DestroyRenderPass) \
    X(CreateFramebuffer) X(DestroyFramebuffer) X(CreateShaderModule) X(DestroyShaderModule) \
    X(CreatePipelineLayout) X(DestroyPipelineLayout) X(CreateGraphicsPipelines) X(DestroyPipeline) \
    X(CreateDescriptorSetLayout) X(DestroyDescriptorSetLayout) X(CreateDescriptorPool) X(DestroyDescriptorPool) \
    X(AllocateDescriptorSets) X(UpdateDescriptorSets) X(CreateSampler) X(DestroySampler) \
    X(CreateImage) X(DestroyImage) X(GetImageMemoryRequirements) X(BindImageMemory) \
    X(CreateBuffer) X(DestroyBuffer) X(GetBufferMemoryRequirements) X(BindBufferMemory) \
    X(AllocateMemory) X(FreeMemory) X(MapMemory) \
    X(CreateCommandPool) X(DestroyCommandPool) X(AllocateCommandBuffers) X(FreeCommandBuffers) \
    X(BeginCommandBuffer) X(EndCommandBuffer) X(ResetCommandBuffer) \
    X(CmdPipelineBarrier) X(CmdCopyBufferToImage) X(CmdBeginRenderPass) X(CmdEndRenderPass) \
    X(CmdBindPipeline) X(CmdBindDescriptorSets) X(CmdSetViewport) X(CmdSetScissor) X(CmdDraw) X(CmdPushConstants) \
    X(CreateSemaphore) X(DestroySemaphore) X(CreateFence) X(DestroyFence) X(WaitForFences) X(ResetFences) \
    X(QueueSubmit)

    struct Fn
    {
#define X(name) PFN_vk##name name;
        FF_VK_FUNCS(X)
#undef X
    };

    // Una per ogni immagine dello swapchain.
    struct Frame
    {
        VkImageView view;
        VkFramebuffer framebuffer;
        VkCommandBuffer cmd;
        VkFence fence;          // segnala che il nostro comando per questa immagine è finito
        VkSemaphore semaphore;  // la presentazione aspetta questo
        bool submitted;
    };

    struct Swapchain
    {
        VkSwapchainKHR handle;
        VkFormat format;
        uint32_t colorMode;   // ff::ColorMode, deciso da formato e spazio colore alla creazione
        VkExtent2D extent;
        bool ready, failed;
        VkRenderPass pass;
        VkPipeline pipeline;
        std::vector<Frame> frames;
    };

    struct QueueInfo { VkQueue queue; uint32_t family; };

    // Tutto ciò che serve per disegnare su un device del gioco.
    struct VkState
    {
        VkDevice device = VK_NULL_HANDLE;
        Fn fn = {};
        PFN_vkSetDeviceLoaderData setLoaderData = nullptr;
        VkPhysicalDeviceMemoryProperties memory = {};
        std::vector<VkQueueFamilyProperties> families;

        SRWLOCK lock = SRWLOCK_INIT;
        std::vector<QueueInfo> queues;
        std::vector<Swapchain*> swapchains;

        bool ready = false, failed = false, complete = false;
        uint32_t poolFamily = 0;
        VkCommandPool pool = VK_NULL_HANDLE;
        VkSampler sampler = VK_NULL_HANDLE;
        VkImage texture = VK_NULL_HANDLE;
        VkDeviceMemory textureMemory = VK_NULL_HANDLE;
        VkImageView textureView = VK_NULL_HANDLE;
        VkBuffer staging = VK_NULL_HANDLE;
        VkDeviceMemory stagingMemory = VK_NULL_HANDLE;
        void* mapped = nullptr;
        VkDescriptorSetLayout setLayout = VK_NULL_HANDLE;
        VkDescriptorPool descriptorPool = VK_NULL_HANDLE;
        VkDescriptorSet descriptorSet = VK_NULL_HANDLE;
        VkPipelineLayout pipelineLayout = VK_NULL_HANDLE;
        VkShaderModule vert = VK_NULL_HANDLE, frag = VK_NULL_HANDLE;
        VkShaderModule fragHdr = VK_NULL_HANDLE;   // variante per gli swapchain HDR (scRGB, HDR10)
        bool textureSrgb = false;                  // la nostra immagine si legge già lineare
        uint32_t textureSerial = 0;
        bool textureFilled = false;
        Frame* uploadFrame = nullptr;   // ultimo comando che ha copiato dal buffer di appoggio
    };

    struct InstanceData
    {
        void* key;
        VkInstance instance;
        PFN_vkGetInstanceProcAddr nextGetProcAddr;
        PFN_vkDestroyInstance destroy;
    };
    struct DeviceData
    {
        void* key;
        PFN_vkGetDeviceProcAddr nextGetProcAddr;
        PFN_vkDestroyDevice destroy;
        PFN_vkQueuePresentKHR present;
        VkState* vk;
    };

    const int kMax = 64;
    InstanceData g_instances[kMax];
    DeviceData g_devices[kMax];
    SRWLOCK g_lock = SRWLOCK_INIT;
    // GetDeviceProcAddr del layer successivo (è la stessa funzione per tutti i device): serve per lasciar
    // passare senza di noi un device che non è entrato nella tabella (tabella piena).
    PFN_vkGetDeviceProcAddr volatile g_nextDeviceProcAddr = nullptr;
    PFN_vkGetInstanceProcAddr volatile g_nextInstanceProcAddr = nullptr;   // lo stesso per le istanze

    template <typename T> T* Find(T* table, void* key)
    {
        for (int i = 0; i < kMax; i++)
            if (table[i].key == key) return &table[i];
        return nullptr;
    }

    template <typename T> T Lookup(T* table, void* key)
    {
        AcquireSRWLockShared(&g_lock);
        T* found = Find(table, key);
        T copy = found ? *found : T{};
        ReleaseSRWLockShared(&g_lock);
        return copy;
    }

    template <typename T> bool Store(T* table, const T& value)
    {
        AcquireSRWLockExclusive(&g_lock);
        T* slot = Find(table, value.key);
        if (!slot) slot = Find(table, nullptr);
        if (slot) *slot = value;
        ReleaseSRWLockExclusive(&g_lock);
        return slot != nullptr;
    }

    template <typename T> void Erase(T* table, void* key)
    {
        AcquireSRWLockExclusive(&g_lock);
        if (T* slot = Find(table, key)) *slot = T{};
        ReleaseSRWLockExclusive(&g_lock);
    }

    struct Guard
    {
        SRWLOCK* lock;
        explicit Guard(SRWLOCK* l) : lock(l) { AcquireSRWLockExclusive(lock); }
        ~Guard() { ReleaseSRWLockExclusive(lock); }
    };

    // ---------------------------------------------------------------- overlay: risorse

    const uint64_t kWaitNs = 1000000000ull;   // attesa massima di un nostro comando precedente: 1 secondo
    const uint32_t kMaxWaits = 64;            // semafori di attesa di una presentazione che sappiamo gestire

    bool IsSrgb(VkFormat f)
    {
        return f == VK_FORMAT_R8G8B8A8_SRGB || f == VK_FORMAT_B8G8R8A8_SRGB || f == VK_FORMAT_A8B8G8R8_SRGB_PACK32;
    }

    bool FindMemory(VkState* vk, uint32_t typeBits, VkMemoryPropertyFlags wanted, uint32_t& index)
    {
        for (uint32_t i = 0; i < vk->memory.memoryTypeCount; i++)
            if ((typeBits & (1u << i)) && (vk->memory.memoryTypes[i].propertyFlags & wanted) == wanted)
            {
                index = i;
                return true;
            }
        return false;
    }

    bool Fail(VkState* vk, const char* step, VkResult r)
    {
        vk->failed = true;
        ff::Log("Vulkan: overlay non disponibile (%s, codice %d)", step, (int)r);
        return false;
    }

    // Risorse comuni a tutti gli swapchain del device. "srgb" dice se il gioco disegna su un
    // formato sRGB: in quel caso anche la nostra immagine lo è, così i colori non cambiano.
    bool InitDevice(VkState* vk, uint32_t family, bool srgb)
    {
        if (vk->ready) return true;
        if (vk->failed || !vk->complete) return false;
        const Fn& fn = vk->fn;
        VkDevice dev = vk->device;
        VkResult r;

        VkCommandPoolCreateInfo pool = { VK_STRUCTURE_TYPE_COMMAND_POOL_CREATE_INFO };
        pool.flags = VK_COMMAND_POOL_CREATE_RESET_COMMAND_BUFFER_BIT;
        pool.queueFamilyIndex = family;
        if ((r = fn.CreateCommandPool(dev, &pool, nullptr, &vk->pool)) != VK_SUCCESS) return Fail(vk, "command pool", r);
        vk->poolFamily = family;

        VkSamplerCreateInfo sampler = { VK_STRUCTURE_TYPE_SAMPLER_CREATE_INFO };
        sampler.magFilter = sampler.minFilter = VK_FILTER_NEAREST;
        sampler.mipmapMode = VK_SAMPLER_MIPMAP_MODE_NEAREST;
        sampler.addressModeU = sampler.addressModeV = sampler.addressModeW = VK_SAMPLER_ADDRESS_MODE_CLAMP_TO_EDGE;
        sampler.maxAnisotropy = 1.0f;
        if ((r = fn.CreateSampler(dev, &sampler, nullptr, &vk->sampler)) != VK_SUCCESS) return Fail(vk, "sampler", r);

        // Immagine grande quanto l'overlay più grande possibile: non va mai ricreata.
        VkFormat format = srgb ? VK_FORMAT_B8G8R8A8_SRGB : VK_FORMAT_B8G8R8A8_UNORM;
        VkImageCreateInfo image = { VK_STRUCTURE_TYPE_IMAGE_CREATE_INFO };
        image.imageType = VK_IMAGE_TYPE_2D;
        image.format = format;
        image.extent = { FF_OV_MAX_W, FF_OV_MAX_H, 1 };
        image.mipLevels = 1;
        image.arrayLayers = 1;
        image.samples = VK_SAMPLE_COUNT_1_BIT;
        image.tiling = VK_IMAGE_TILING_OPTIMAL;
        image.usage = VK_IMAGE_USAGE_SAMPLED_BIT | VK_IMAGE_USAGE_TRANSFER_DST_BIT;
        image.sharingMode = VK_SHARING_MODE_EXCLUSIVE;
        image.initialLayout = VK_IMAGE_LAYOUT_UNDEFINED;
        if ((r = fn.CreateImage(dev, &image, nullptr, &vk->texture)) != VK_SUCCESS) return Fail(vk, "immagine", r);

        VkMemoryRequirements need;
        fn.GetImageMemoryRequirements(dev, vk->texture, &need);
        VkMemoryAllocateInfo alloc = { VK_STRUCTURE_TYPE_MEMORY_ALLOCATE_INFO };
        alloc.allocationSize = need.size;
        if (!FindMemory(vk, need.memoryTypeBits, VK_MEMORY_PROPERTY_DEVICE_LOCAL_BIT, alloc.memoryTypeIndex) &&
            !FindMemory(vk, need.memoryTypeBits, 0, alloc.memoryTypeIndex))
            return Fail(vk, "tipo di memoria dell'immagine", VK_ERROR_FEATURE_NOT_PRESENT);
        if ((r = fn.AllocateMemory(dev, &alloc, nullptr, &vk->textureMemory)) != VK_SUCCESS) return Fail(vk, "memoria dell'immagine", r);
        if ((r = fn.BindImageMemory(dev, vk->texture, vk->textureMemory, 0)) != VK_SUCCESS) return Fail(vk, "bind dell'immagine", r);

        VkImageViewCreateInfo view = { VK_STRUCTURE_TYPE_IMAGE_VIEW_CREATE_INFO };
        view.image = vk->texture;
        view.viewType = VK_IMAGE_VIEW_TYPE_2D;
        view.format = format;
        view.subresourceRange = { VK_IMAGE_ASPECT_COLOR_BIT, 0, 1, 0, 1 };
        if ((r = fn.CreateImageView(dev, &view, nullptr, &vk->textureView)) != VK_SUCCESS) return Fail(vk, "vista dell'immagine", r);

        // Buffer di appoggio: l'immagine dell'app passa da qui per arrivare alla scheda video.
        VkBufferCreateInfo buffer = { VK_STRUCTURE_TYPE_BUFFER_CREATE_INFO };
        buffer.size = (VkDeviceSize)FF_OV_MAX_W * FF_OV_MAX_H * 4;
        buffer.usage = VK_BUFFER_USAGE_TRANSFER_SRC_BIT;
        buffer.sharingMode = VK_SHARING_MODE_EXCLUSIVE;
        if ((r = fn.CreateBuffer(dev, &buffer, nullptr, &vk->staging)) != VK_SUCCESS) return Fail(vk, "buffer", r);
        fn.GetBufferMemoryRequirements(dev, vk->staging, &need);
        alloc.allocationSize = need.size;
        if (!FindMemory(vk, need.memoryTypeBits, VK_MEMORY_PROPERTY_HOST_VISIBLE_BIT | VK_MEMORY_PROPERTY_HOST_COHERENT_BIT, alloc.memoryTypeIndex))
            return Fail(vk, "tipo di memoria del buffer", VK_ERROR_FEATURE_NOT_PRESENT);
        if ((r = fn.AllocateMemory(dev, &alloc, nullptr, &vk->stagingMemory)) != VK_SUCCESS) return Fail(vk, "memoria del buffer", r);
        if ((r = fn.BindBufferMemory(dev, vk->staging, vk->stagingMemory, 0)) != VK_SUCCESS) return Fail(vk, "bind del buffer", r);
        if ((r = fn.MapMemory(dev, vk->stagingMemory, 0, VK_WHOLE_SIZE, 0, &vk->mapped)) != VK_SUCCESS) return Fail(vk, "mappatura del buffer", r);

        VkDescriptorSetLayoutBinding binding = {};
        binding.binding = 0;
        binding.descriptorType = VK_DESCRIPTOR_TYPE_COMBINED_IMAGE_SAMPLER;
        binding.descriptorCount = 1;
        binding.stageFlags = VK_SHADER_STAGE_FRAGMENT_BIT;
        VkDescriptorSetLayoutCreateInfo setLayout = { VK_STRUCTURE_TYPE_DESCRIPTOR_SET_LAYOUT_CREATE_INFO };
        setLayout.bindingCount = 1;
        setLayout.pBindings = &binding;
        if ((r = fn.CreateDescriptorSetLayout(dev, &setLayout, nullptr, &vk->setLayout)) != VK_SUCCESS) return Fail(vk, "layout dei descrittori", r);

        VkDescriptorPoolSize poolSize = { VK_DESCRIPTOR_TYPE_COMBINED_IMAGE_SAMPLER, 1 };
        VkDescriptorPoolCreateInfo descPool = { VK_STRUCTURE_TYPE_DESCRIPTOR_POOL_CREATE_INFO };
        descPool.maxSets = 1;
        descPool.poolSizeCount = 1;
        descPool.pPoolSizes = &poolSize;
        if ((r = fn.CreateDescriptorPool(dev, &descPool, nullptr, &vk->descriptorPool)) != VK_SUCCESS) return Fail(vk, "pool dei descrittori", r);

        VkDescriptorSetAllocateInfo setAlloc = { VK_STRUCTURE_TYPE_DESCRIPTOR_SET_ALLOCATE_INFO };
        setAlloc.descriptorPool = vk->descriptorPool;
        setAlloc.descriptorSetCount = 1;
        setAlloc.pSetLayouts = &vk->setLayout;
        if ((r = fn.AllocateDescriptorSets(dev, &setAlloc, &vk->descriptorSet)) != VK_SUCCESS) return Fail(vk, "descrittore", r);

        VkDescriptorImageInfo imageInfo = { vk->sampler, vk->textureView, VK_IMAGE_LAYOUT_SHADER_READ_ONLY_OPTIMAL };
        VkWriteDescriptorSet write = { VK_STRUCTURE_TYPE_WRITE_DESCRIPTOR_SET };
        write.dstSet = vk->descriptorSet;
        write.dstBinding = 0;
        write.descriptorCount = 1;
        write.descriptorType = VK_DESCRIPTOR_TYPE_COMBINED_IMAGE_SAMPLER;
        write.pImageInfo = &imageInfo;
        fn.UpdateDescriptorSets(dev, 1, &write, 0, nullptr);

        // Costanti del colore (ff::ColorConsts) per la variante HDR del fragment shader.
        VkPushConstantRange push = { VK_SHADER_STAGE_FRAGMENT_BIT, 0, sizeof(ff::ColorConsts) };
        VkPipelineLayoutCreateInfo layout = { VK_STRUCTURE_TYPE_PIPELINE_LAYOUT_CREATE_INFO };
        layout.setLayoutCount = 1;
        layout.pSetLayouts = &vk->setLayout;
        layout.pushConstantRangeCount = 1;
        layout.pPushConstantRanges = &push;
        if ((r = fn.CreatePipelineLayout(dev, &layout, nullptr, &vk->pipelineLayout)) != VK_SUCCESS) return Fail(vk, "layout della pipeline", r);

        VkShaderModuleCreateInfo shader = { VK_STRUCTURE_TYPE_SHADER_MODULE_CREATE_INFO };
        shader.codeSize = sizeof(kOverlayVertSpv);
        shader.pCode = kOverlayVertSpv;
        if ((r = fn.CreateShaderModule(dev, &shader, nullptr, &vk->vert)) != VK_SUCCESS) return Fail(vk, "vertex shader", r);
        shader.codeSize = sizeof(kOverlayFragSpv);
        shader.pCode = kOverlayFragSpv;
        if ((r = fn.CreateShaderModule(dev, &shader, nullptr, &vk->frag)) != VK_SUCCESS) return Fail(vk, "fragment shader", r);
        shader.codeSize = sizeof(kOverlayFragHdrSpv);
        shader.pCode = kOverlayFragHdrSpv;
        if ((r = fn.CreateShaderModule(dev, &shader, nullptr, &vk->fragHdr)) != VK_SUCCESS) return Fail(vk, "fragment shader HDR", r);
        vk->textureSrgb = srgb;

        vk->ready = true;
        ff::Log("Vulkan: risorse dell'overlay create (%s)", srgb ? "sRGB" : "UNORM");
        return true;
    }

    bool FailSwapchain(Swapchain* sc, const char* step, VkResult r)
    {
        sc->failed = true;
        ff::Log("Vulkan: overlay non disponibile su questo swapchain (%s, codice %d)", step, (int)r);
        return false;
    }

    // Risorse legate a uno swapchain: dipendono dal suo formato e dalle sue immagini.
    bool InitSwapchain(VkState* vk, Swapchain* sc)
    {
        if (sc->ready) return true;
        if (sc->failed) return false;
        const Fn& fn = vk->fn;
        VkDevice dev = vk->device;
        VkResult r;

        uint32_t count = 0;
        if ((r = fn.GetSwapchainImagesKHR(dev, sc->handle, &count, nullptr)) != VK_SUCCESS || !count) return FailSwapchain(sc, "numero di immagini", r);
        std::vector<VkImage> images(count);
        if ((r = fn.GetSwapchainImagesKHR(dev, sc->handle, &count, images.data())) != VK_SUCCESS) return FailSwapchain(sc, "immagini", r);

        // Si disegna sopra il fotogramma già pronto per lo schermo, e lo si lascia pronto per lo schermo.
        VkAttachmentDescription attachment = {};
        attachment.format = sc->format;
        attachment.samples = VK_SAMPLE_COUNT_1_BIT;
        attachment.loadOp = VK_ATTACHMENT_LOAD_OP_LOAD;
        attachment.storeOp = VK_ATTACHMENT_STORE_OP_STORE;
        attachment.stencilLoadOp = VK_ATTACHMENT_LOAD_OP_DONT_CARE;
        attachment.stencilStoreOp = VK_ATTACHMENT_STORE_OP_DONT_CARE;
        attachment.initialLayout = VK_IMAGE_LAYOUT_PRESENT_SRC_KHR;
        attachment.finalLayout = VK_IMAGE_LAYOUT_PRESENT_SRC_KHR;
        VkAttachmentReference ref = { 0, VK_IMAGE_LAYOUT_COLOR_ATTACHMENT_OPTIMAL };
        VkSubpassDescription subpass = {};
        subpass.pipelineBindPoint = VK_PIPELINE_BIND_POINT_GRAPHICS;
        subpass.colorAttachmentCount = 1;
        subpass.pColorAttachments = &ref;
        VkSubpassDependency dependency = {};
        dependency.srcSubpass = VK_SUBPASS_EXTERNAL;
        dependency.dstSubpass = 0;
        dependency.srcStageMask = VK_PIPELINE_STAGE_COLOR_ATTACHMENT_OUTPUT_BIT;
        dependency.dstStageMask = VK_PIPELINE_STAGE_COLOR_ATTACHMENT_OUTPUT_BIT;
        dependency.srcAccessMask = 0;
        dependency.dstAccessMask = VK_ACCESS_COLOR_ATTACHMENT_READ_BIT | VK_ACCESS_COLOR_ATTACHMENT_WRITE_BIT;
        VkRenderPassCreateInfo pass = { VK_STRUCTURE_TYPE_RENDER_PASS_CREATE_INFO };
        pass.attachmentCount = 1;
        pass.pAttachments = &attachment;
        pass.subpassCount = 1;
        pass.pSubpasses = &subpass;
        pass.dependencyCount = 1;
        pass.pDependencies = &dependency;
        if ((r = fn.CreateRenderPass(dev, &pass, nullptr, &sc->pass)) != VK_SUCCESS) return FailSwapchain(sc, "render pass", r);

        VkPipelineShaderStageCreateInfo stages[2] = {};
        stages[0].sType = stages[1].sType = VK_STRUCTURE_TYPE_PIPELINE_SHADER_STAGE_CREATE_INFO;
        stages[0].stage = VK_SHADER_STAGE_VERTEX_BIT;
        stages[0].module = vk->vert;
        stages[0].pName = "main";
        stages[1].stage = VK_SHADER_STAGE_FRAGMENT_BIT;
        bool hdr = sc->colorMode == ff::kColorScRgb || sc->colorMode == ff::kColorHdr10;
        stages[1].module = hdr ? vk->fragHdr : vk->frag;
        stages[1].pName = "main";
        VkPipelineVertexInputStateCreateInfo vertexInput = { VK_STRUCTURE_TYPE_PIPELINE_VERTEX_INPUT_STATE_CREATE_INFO };
        VkPipelineInputAssemblyStateCreateInfo assembly = { VK_STRUCTURE_TYPE_PIPELINE_INPUT_ASSEMBLY_STATE_CREATE_INFO };
        assembly.topology = VK_PRIMITIVE_TOPOLOGY_TRIANGLE_STRIP;
        VkPipelineViewportStateCreateInfo viewport = { VK_STRUCTURE_TYPE_PIPELINE_VIEWPORT_STATE_CREATE_INFO };
        viewport.viewportCount = 1;
        viewport.scissorCount = 1;
        VkPipelineRasterizationStateCreateInfo raster = { VK_STRUCTURE_TYPE_PIPELINE_RASTERIZATION_STATE_CREATE_INFO };
        raster.polygonMode = VK_POLYGON_MODE_FILL;
        raster.cullMode = VK_CULL_MODE_NONE;
        raster.frontFace = VK_FRONT_FACE_COUNTER_CLOCKWISE;
        raster.lineWidth = 1.0f;
        VkPipelineMultisampleStateCreateInfo multisample = { VK_STRUCTURE_TYPE_PIPELINE_MULTISAMPLE_STATE_CREATE_INFO };
        multisample.rasterizationSamples = VK_SAMPLE_COUNT_1_BIT;
        // L'immagine dell'app ha l'alfa già moltiplicato nei colori.
        VkPipelineColorBlendAttachmentState blendTarget = {};
        blendTarget.blendEnable = VK_TRUE;
        blendTarget.srcColorBlendFactor = VK_BLEND_FACTOR_ONE;
        blendTarget.dstColorBlendFactor = VK_BLEND_FACTOR_ONE_MINUS_SRC_ALPHA;
        blendTarget.colorBlendOp = VK_BLEND_OP_ADD;
        blendTarget.srcAlphaBlendFactor = VK_BLEND_FACTOR_ONE;
        blendTarget.dstAlphaBlendFactor = VK_BLEND_FACTOR_ONE_MINUS_SRC_ALPHA;
        blendTarget.alphaBlendOp = VK_BLEND_OP_ADD;
        blendTarget.colorWriteMask = VK_COLOR_COMPONENT_R_BIT | VK_COLOR_COMPONENT_G_BIT | VK_COLOR_COMPONENT_B_BIT | VK_COLOR_COMPONENT_A_BIT;
        VkPipelineColorBlendStateCreateInfo blend = { VK_STRUCTURE_TYPE_PIPELINE_COLOR_BLEND_STATE_CREATE_INFO };
        blend.attachmentCount = 1;
        blend.pAttachments = &blendTarget;
        VkDynamicState dynamicStates[] = { VK_DYNAMIC_STATE_VIEWPORT, VK_DYNAMIC_STATE_SCISSOR };
        VkPipelineDynamicStateCreateInfo dynamic = { VK_STRUCTURE_TYPE_PIPELINE_DYNAMIC_STATE_CREATE_INFO };
        dynamic.dynamicStateCount = 2;
        dynamic.pDynamicStates = dynamicStates;
        VkGraphicsPipelineCreateInfo pipeline = { VK_STRUCTURE_TYPE_GRAPHICS_PIPELINE_CREATE_INFO };
        pipeline.stageCount = 2;
        pipeline.pStages = stages;
        pipeline.pVertexInputState = &vertexInput;
        pipeline.pInputAssemblyState = &assembly;
        pipeline.pViewportState = &viewport;
        pipeline.pRasterizationState = &raster;
        pipeline.pMultisampleState = &multisample;
        pipeline.pColorBlendState = &blend;
        pipeline.pDynamicState = &dynamic;
        pipeline.layout = vk->pipelineLayout;
        pipeline.renderPass = sc->pass;
        pipeline.subpass = 0;
        if ((r = fn.CreateGraphicsPipelines(dev, VK_NULL_HANDLE, 1, &pipeline, nullptr, &sc->pipeline)) != VK_SUCCESS) return FailSwapchain(sc, "pipeline", r);

        sc->frames.assign(count, Frame{});
        for (uint32_t i = 0; i < count; i++)
        {
            Frame& f = sc->frames[i];
            VkImageViewCreateInfo view = { VK_STRUCTURE_TYPE_IMAGE_VIEW_CREATE_INFO };
            view.image = images[i];
            view.viewType = VK_IMAGE_VIEW_TYPE_2D;
            view.format = sc->format;
            view.subresourceRange = { VK_IMAGE_ASPECT_COLOR_BIT, 0, 1, 0, 1 };
            if ((r = fn.CreateImageView(dev, &view, nullptr, &f.view)) != VK_SUCCESS) return FailSwapchain(sc, "vista del fotogramma", r);

            VkFramebufferCreateInfo fb = { VK_STRUCTURE_TYPE_FRAMEBUFFER_CREATE_INFO };
            fb.renderPass = sc->pass;
            fb.attachmentCount = 1;
            fb.pAttachments = &f.view;
            fb.width = sc->extent.width;
            fb.height = sc->extent.height;
            fb.layers = 1;
            if ((r = fn.CreateFramebuffer(dev, &fb, nullptr, &f.framebuffer)) != VK_SUCCESS) return FailSwapchain(sc, "framebuffer", r);

            VkCommandBufferAllocateInfo cmd = { VK_STRUCTURE_TYPE_COMMAND_BUFFER_ALLOCATE_INFO };
            cmd.commandPool = vk->pool;
            cmd.level = VK_COMMAND_BUFFER_LEVEL_PRIMARY;
            cmd.commandBufferCount = 1;
            if ((r = fn.AllocateCommandBuffers(dev, &cmd, &f.cmd)) != VK_SUCCESS) return FailSwapchain(sc, "command buffer", r);
            // Gli oggetti "smistabili" creati da un layer vanno presentati al loader, o non sono utilizzabili.
            if ((r = vk->setLoaderData(dev, f.cmd)) != VK_SUCCESS) return FailSwapchain(sc, "registrazione del command buffer", r);

            VkFenceCreateInfo fence = { VK_STRUCTURE_TYPE_FENCE_CREATE_INFO };
            if ((r = fn.CreateFence(dev, &fence, nullptr, &f.fence)) != VK_SUCCESS) return FailSwapchain(sc, "fence", r);
            VkSemaphoreCreateInfo semaphore = { VK_STRUCTURE_TYPE_SEMAPHORE_CREATE_INFO };
            if ((r = fn.CreateSemaphore(dev, &semaphore, nullptr, &f.semaphore)) != VK_SUCCESS) return FailSwapchain(sc, "semaforo", r);
        }

        sc->ready = true;
        ff::Log("Vulkan: overlay pronto (%ux%u, formato %d, %u immagini, colori %s)", sc->extent.width, sc->extent.height,
            (int)sc->format, count, ff::ColorModeName(sc->colorMode));
        return true;
    }

    void DestroySwapchainResources(VkState* vk, Swapchain* sc)
    {
        const Fn& fn = vk->fn;
        VkDevice dev = vk->device;
        for (Frame& f : sc->frames)
        {
            if (f.submitted && f.fence) fn.WaitForFences(dev, 1, &f.fence, VK_TRUE, kWaitNs);
            if (vk->uploadFrame == &f) vk->uploadFrame = nullptr;
            if (f.framebuffer) fn.DestroyFramebuffer(dev, f.framebuffer, nullptr);
            if (f.view) fn.DestroyImageView(dev, f.view, nullptr);
            if (f.cmd) fn.FreeCommandBuffers(dev, vk->pool, 1, &f.cmd);
            if (f.fence) fn.DestroyFence(dev, f.fence, nullptr);
            if (f.semaphore) fn.DestroySemaphore(dev, f.semaphore, nullptr);
        }
        sc->frames.clear();
        if (sc->pipeline) fn.DestroyPipeline(dev, sc->pipeline, nullptr);
        if (sc->pass) fn.DestroyRenderPass(dev, sc->pass, nullptr);
        sc->pipeline = VK_NULL_HANDLE;
        sc->pass = VK_NULL_HANDLE;
        sc->ready = false;
    }

    void DestroyDeviceResources(VkState* vk)
    {
        if (!vk->complete) return;
        const Fn& fn = vk->fn;
        VkDevice dev = vk->device;
        for (Swapchain* sc : vk->swapchains)
        {
            DestroySwapchainResources(vk, sc);
            delete sc;
        }
        vk->swapchains.clear();
        if (vk->vert) fn.DestroyShaderModule(dev, vk->vert, nullptr);
        if (vk->frag) fn.DestroyShaderModule(dev, vk->frag, nullptr);
        if (vk->fragHdr) fn.DestroyShaderModule(dev, vk->fragHdr, nullptr);
        if (vk->pipelineLayout) fn.DestroyPipelineLayout(dev, vk->pipelineLayout, nullptr);
        if (vk->descriptorPool) fn.DestroyDescriptorPool(dev, vk->descriptorPool, nullptr);
        if (vk->setLayout) fn.DestroyDescriptorSetLayout(dev, vk->setLayout, nullptr);
        if (vk->staging) fn.DestroyBuffer(dev, vk->staging, nullptr);
        if (vk->stagingMemory) fn.FreeMemory(dev, vk->stagingMemory, nullptr);
        if (vk->textureView) fn.DestroyImageView(dev, vk->textureView, nullptr);
        if (vk->texture) fn.DestroyImage(dev, vk->texture, nullptr);
        if (vk->textureMemory) fn.FreeMemory(dev, vk->textureMemory, nullptr);
        if (vk->sampler) fn.DestroySampler(dev, vk->sampler, nullptr);
        if (vk->pool) fn.DestroyCommandPool(dev, vk->pool, nullptr);
    }

    // ---------------------------------------------------------------- overlay: disegno

    // Disegna l'overlay sull'immagine che sta per essere presentata. Se riesce, "wait" è il
    // semaforo che la presentazione deve aspettare al posto di quelli del gioco.
    bool Draw(VkState* vk, VkQueue queue, const VkPresentInfoKHR* info, const ff::Overlay& ov, VkSemaphore* wait)
    {
        if (!vk || vk->failed || !vk->complete || info->swapchainCount == 0) return false;
        if (info->waitSemaphoreCount > kMaxWaits) return false;   // caso limite: niente overlay, presentazione intatta
        const Fn& fn = vk->fn;
        VkDevice dev = vk->device;

        // La coda con cui il gioco presenta deve saper anche disegnare (è così quasi sempre).
        uint32_t family = UINT32_MAX;
        for (const QueueInfo& q : vk->queues)
            if (q.queue == queue) family = q.family;
        if (family >= vk->families.size() || !(vk->families[family].queueFlags & VK_QUEUE_GRAPHICS_BIT))
        {
            vk->failed = true;
            ff::Log("Vulkan: overlay non disponibile (la coda di presentazione non disegna)");
            return false;
        }

        Swapchain* sc = nullptr;
        for (Swapchain* s : vk->swapchains)
            if (s->handle == info->pSwapchains[0]) sc = s;
        if (!sc) return false;

        if (!InitDevice(vk, family, IsSrgb(sc->format))) return false;
        if (vk->poolFamily != family) return false;
        if (!InitSwapchain(vk, sc)) return false;

        uint32_t index = info->pImageIndices[0];
        if (index >= sc->frames.size()) return false;
        Frame& f = sc->frames[index];

        // Il comando precedente su questa immagine deve essere finito prima di riscriverlo.
        if (f.submitted)
        {
            if (fn.WaitForFences(dev, 1, &f.fence, VK_TRUE, kWaitNs) != VK_SUCCESS) return false;
            fn.ResetFences(dev, 1, &f.fence);
            f.submitted = false;
        }

        int x, y;
        ff::Place(ov, sc->extent.width, sc->extent.height, x, y);
        int left = x < 0 ? 0 : x, top = y < 0 ? 0 : y;
        int right = x + (int)ov.width, bottom = y + (int)ov.height;
        if (right > (int)sc->extent.width) right = (int)sc->extent.width;
        if (bottom > (int)sc->extent.height) bottom = (int)sc->extent.height;
        if (right <= left || bottom <= top) return false;

        bool upload = !vk->textureFilled || vk->textureSerial != ov.serial;
        uint32_t y0 = 0, y1 = ov.height;
        if (upload)
        {
            // Il buffer di appoggio non va riscritto mentre la scheda video lo sta ancora leggendo: se la
            // copia precedente non è finita non si aspetta (timeout 0), si ricarica al prossimo frame.
            if (vk->uploadFrame && vk->uploadFrame->submitted &&
                fn.WaitForFences(dev, 1, &vk->uploadFrame->fence, VK_TRUE, 0) != VK_SUCCESS)
                upload = false;
        }
        if (upload)
        {
            // Solo le righe cambiate (di solito la striscia del grafico); tutte se la texture è nuova.
            ff::RowsToUpload(ov, vk->textureSerial, !vk->textureFilled, y0, y1);
            size_t row = (size_t)ov.width * 4;
            memcpy((uint8_t*)vk->mapped + y0 * row, ov.pixels + y0 * row, (y1 - y0) * row);
        }

        if (fn.ResetCommandBuffer(f.cmd, 0) != VK_SUCCESS) return false;
        VkCommandBufferBeginInfo begin = { VK_STRUCTURE_TYPE_COMMAND_BUFFER_BEGIN_INFO };
        begin.flags = VK_COMMAND_BUFFER_USAGE_ONE_TIME_SUBMIT_BIT;
        if (fn.BeginCommandBuffer(f.cmd, &begin) != VK_SUCCESS) return false;

        if (upload)
        {
            VkImageMemoryBarrier barrier = { VK_STRUCTURE_TYPE_IMAGE_MEMORY_BARRIER };
            barrier.srcAccessMask = 0;
            barrier.dstAccessMask = VK_ACCESS_TRANSFER_WRITE_BIT;
            // Con un aggiornamento parziale il resto dell'immagine va conservato; la prima volta no.
            barrier.oldLayout = vk->textureFilled ? VK_IMAGE_LAYOUT_SHADER_READ_ONLY_OPTIMAL : VK_IMAGE_LAYOUT_UNDEFINED;
            barrier.newLayout = VK_IMAGE_LAYOUT_TRANSFER_DST_OPTIMAL;
            barrier.srcQueueFamilyIndex = barrier.dstQueueFamilyIndex = VK_QUEUE_FAMILY_IGNORED;
            barrier.image = vk->texture;
            barrier.subresourceRange = { VK_IMAGE_ASPECT_COLOR_BIT, 0, 1, 0, 1 };
            fn.CmdPipelineBarrier(f.cmd, VK_PIPELINE_STAGE_FRAGMENT_SHADER_BIT, VK_PIPELINE_STAGE_TRANSFER_BIT, 0, 0, nullptr, 0, nullptr, 1, &barrier);

            VkBufferImageCopy region = {};
            region.bufferOffset = (VkDeviceSize)y0 * ov.width * 4;
            region.bufferRowLength = ov.width;
            region.bufferImageHeight = y1 - y0;
            region.imageSubresource = { VK_IMAGE_ASPECT_COLOR_BIT, 0, 0, 1 };
            region.imageOffset = { 0, (int32_t)y0, 0 };
            region.imageExtent = { ov.width, y1 - y0, 1 };
            fn.CmdCopyBufferToImage(f.cmd, vk->staging, vk->texture, VK_IMAGE_LAYOUT_TRANSFER_DST_OPTIMAL, 1, &region);

            barrier.srcAccessMask = VK_ACCESS_TRANSFER_WRITE_BIT;
            barrier.dstAccessMask = VK_ACCESS_SHADER_READ_BIT;
            barrier.oldLayout = VK_IMAGE_LAYOUT_TRANSFER_DST_OPTIMAL;
            barrier.newLayout = VK_IMAGE_LAYOUT_SHADER_READ_ONLY_OPTIMAL;
            fn.CmdPipelineBarrier(f.cmd, VK_PIPELINE_STAGE_TRANSFER_BIT, VK_PIPELINE_STAGE_FRAGMENT_SHADER_BIT, 0, 0, nullptr, 0, nullptr, 1, &barrier);
        }

        VkRenderPassBeginInfo pass = { VK_STRUCTURE_TYPE_RENDER_PASS_BEGIN_INFO };
        pass.renderPass = sc->pass;
        pass.framebuffer = f.framebuffer;
        pass.renderArea = { { 0, 0 }, sc->extent };
        fn.CmdBeginRenderPass(f.cmd, &pass, VK_SUBPASS_CONTENTS_INLINE);
        fn.CmdBindPipeline(f.cmd, VK_PIPELINE_BIND_POINT_GRAPHICS, sc->pipeline);
        fn.CmdBindDescriptorSets(f.cmd, VK_PIPELINE_BIND_POINT_GRAPHICS, vk->pipelineLayout, 0, 1, &vk->descriptorSet, 0, nullptr);
        if (sc->colorMode == ff::kColorScRgb || sc->colorMode == ff::kColorHdr10)
        {
            // pad0 = 1: la nostra immagine è sRGB e la scheda video la legge già lineare (niente doppia decodifica).
            ff::ColorConsts cc = ff::MakeColorConsts(sc->colorMode, ov.whiteNits);
            cc.pad0 = vk->textureSrgb ? 1.0f : 0.0f;
            fn.CmdPushConstants(f.cmd, vk->pipelineLayout, VK_SHADER_STAGE_FRAGMENT_BIT, 0, sizeof(cc), &cc);
        }
        // L'area di disegno copre l'intera immagine di appoggio, un pixel dello schermo per ogni suo
        // pixel; il ritaglio lascia passare solo la parte che contiene davvero l'overlay.
        VkViewport viewport = { (float)x, (float)y, (float)FF_OV_MAX_W, (float)FF_OV_MAX_H, 0.0f, 1.0f };
        VkRect2D scissor = { { left, top }, { (uint32_t)(right - left), (uint32_t)(bottom - top) } };
        fn.CmdSetViewport(f.cmd, 0, 1, &viewport);
        fn.CmdSetScissor(f.cmd, 0, 1, &scissor);
        fn.CmdDraw(f.cmd, 4, 1, 0, 0);
        fn.CmdEndRenderPass(f.cmd);
        if (fn.EndCommandBuffer(f.cmd) != VK_SUCCESS) return false;

        VkPipelineStageFlags stages[kMaxWaits];   // niente allocazioni a ogni frame
        for (uint32_t i = 0; i < info->waitSemaphoreCount; i++) stages[i] = VK_PIPELINE_STAGE_COLOR_ATTACHMENT_OUTPUT_BIT;
        VkSubmitInfo submit = { VK_STRUCTURE_TYPE_SUBMIT_INFO };
        submit.waitSemaphoreCount = info->waitSemaphoreCount;
        submit.pWaitSemaphores = info->pWaitSemaphores;
        submit.pWaitDstStageMask = stages;
        submit.commandBufferCount = 1;
        submit.pCommandBuffers = &f.cmd;
        submit.signalSemaphoreCount = 1;
        submit.pSignalSemaphores = &f.semaphore;
        VkResult r = fn.QueueSubmit(queue, 1, &submit, f.fence);
        if (r != VK_SUCCESS)
        {
            vk->failed = true;
            ff::Log("Vulkan: overlay non disponibile (invio del comando, codice %d)", (int)r);
            return false;
        }

        f.submitted = true;
        if (upload)
        {
            vk->uploadFrame = &f;
            vk->textureSerial = ov.serial;
            vk->textureFilled = true;
        }
        *wait = f.semaphore;
        ff::MarkDrawn();
        return true;
    }

    // ---------------------------------------------------------------- istanza

    VKAPI_ATTR VkResult VKAPI_CALL CreateInstance(const VkInstanceCreateInfo* info, const VkAllocationCallbacks* alloc, VkInstance* instance)
    {
        // Nella catena pNext il loader lascia il collegamento al layer successivo.
        auto* link = (VkLayerInstanceCreateInfo*)info->pNext;
        while (link && !(link->sType == VK_STRUCTURE_TYPE_LOADER_INSTANCE_CREATE_INFO && link->function == VK_LAYER_LINK_INFO))
            link = (VkLayerInstanceCreateInfo*)link->pNext;
        if (!link || !link->u.pLayerInfo) return VK_ERROR_INITIALIZATION_FAILED;

        PFN_vkGetInstanceProcAddr next = link->u.pLayerInfo->pfnNextGetInstanceProcAddr;
        link->u.pLayerInfo = link->u.pLayerInfo->pNext;   // il layer successivo deve vedere il suo collegamento

        // Il layer è stato accettato (vkNegotiateLoaderLayerInterfaceVersion): da qui la DLL resta caricata
        // e la memoria condivisa esiste prima del primo frame del gioco.
        ff::StartLayerMode();
        g_nextInstanceProcAddr = next;

        auto create = (PFN_vkCreateInstance)next(VK_NULL_HANDLE, "vkCreateInstance");
        if (!create) return VK_ERROR_INITIALIZATION_FAILED;
        VkResult result = create(info, alloc, instance);
        if (result != VK_SUCCESS) return result;

        InstanceData data = { Key(*instance), *instance, next, (PFN_vkDestroyInstance)next(*instance, "vkDestroyInstance") };
        if (Store(g_instances, data)) ff::Log("Vulkan: layer attivo sull'istanza del gioco");
        else ff::Log("Vulkan: troppe istanze, questa passa senza layer");
        return VK_SUCCESS;
    }

    VKAPI_ATTR void VKAPI_CALL DestroyInstance(VkInstance instance, const VkAllocationCallbacks* alloc)
    {
        if (!instance) return;
        InstanceData data = Lookup(g_instances, Key(instance));
        Erase(g_instances, Key(instance));
        if (data.destroy) data.destroy(instance, alloc);
    }

    // ---------------------------------------------------------------- device

    VKAPI_ATTR VkResult VKAPI_CALL QueuePresent(VkQueue queue, const VkPresentInfoKHR* info)
    {
        DeviceData data = Lookup(g_devices, Key(queue));
        // Device sconosciuto (corsa con DestroyDevice): GetDeviceProcAddr non ci mette in mezzo ai device
        // che non conosciamo, quindi qui si arriva solo per errore del gioco.
        if (!data.present) return VK_ERROR_DEVICE_LOST;

        // Se un'altra API presenta già i frame del gioco (es. Vulkan HDR presentato dal driver via DXGI)
        // overlay e limitatore restano a lei.
        if (!ff::OwnsFrames(FF_API_VULKAN)) return data.present(queue, info);

        ff::OnFrame(FF_API_VULKAN);
        if (data.vk && info)
        {
            // Due thread che presentano insieme: uno solo disegna, l'altro salta l'overlay (mai attese).
            ff::OverlayTryLock overlayLock;
            ff::Overlay ov;
            if (overlayLock.locked && ff::GetOverlay(ov))
            {
                VkSemaphore wait = VK_NULL_HANDLE;
                bool drawn = false;
                if (TryAcquireSRWLockExclusive(&data.vk->lock))
                {
                    drawn = Draw(data.vk, queue, info, ov, &wait);
                    ReleaseSRWLockExclusive(&data.vk->lock);
                }
                if (drawn)
                {
                    VkPresentInfoKHR copy = *info;
                    copy.waitSemaphoreCount = 1;
                    copy.pWaitSemaphores = &wait;
                    return data.present(queue, &copy);
                }
            }
        }
        return data.present(queue, info);
    }

    VKAPI_ATTR VkResult VKAPI_CALL CreateSwapchain(VkDevice device, const VkSwapchainCreateInfoKHR* info, const VkAllocationCallbacks* alloc, VkSwapchainKHR* swapchain)
    {
        DeviceData data = Lookup(g_devices, Key(device));
        if (!data.vk || !data.vk->fn.CreateSwapchainKHR) return VK_ERROR_INITIALIZATION_FAILED;

        // Per disegnarci sopra, le immagini devono essere utilizzabili come bersaglio di disegno
        // (lo sono quasi sempre già: è l'uso che ogni schermo deve garantire).
        VkSwapchainCreateInfoKHR copy = *info;
        copy.imageUsage |= VK_IMAGE_USAGE_COLOR_ATTACHMENT_BIT;
        VkResult result = data.vk->fn.CreateSwapchainKHR(device, &copy, alloc, swapchain);
        if (result != VK_SUCCESS) return result;

        auto* sc = new Swapchain{};
        sc->handle = *swapchain;
        sc->format = info->imageFormat;
        // Spazio colore dello swapchain: con HDR l'overlay va convertito (vedi lo shader HDR).
        sc->colorMode = ff::kColorSdr;
        if (info->imageColorSpace == VK_COLOR_SPACE_EXTENDED_SRGB_LINEAR_EXT) sc->colorMode = ff::kColorScRgb;
        else if (info->imageColorSpace == VK_COLOR_SPACE_HDR10_ST2084_EXT) sc->colorMode = ff::kColorHdr10;
        sc->extent = info->imageExtent;
        Guard guard(&data.vk->lock);
        data.vk->swapchains.push_back(sc);
        return VK_SUCCESS;
    }

    VKAPI_ATTR void VKAPI_CALL DestroySwapchain(VkDevice device, VkSwapchainKHR swapchain, const VkAllocationCallbacks* alloc)
    {
        DeviceData data = Lookup(g_devices, Key(device));
        if (!data.vk) return;
        {
            Guard guard(&data.vk->lock);
            auto& list = data.vk->swapchains;
            for (size_t i = 0; i < list.size(); i++)
            {
                if (list[i]->handle != swapchain) continue;
                DestroySwapchainResources(data.vk, list[i]);
                delete list[i];
                list.erase(list.begin() + i);
                break;
            }
        }
        if (data.vk->fn.DestroySwapchainKHR) data.vk->fn.DestroySwapchainKHR(device, swapchain, alloc);
    }

    void RememberQueue(VkState* vk, VkQueue queue, uint32_t family)
    {
        if (!queue) return;
        Guard guard(&vk->lock);
        for (QueueInfo& q : vk->queues)
            if (q.queue == queue) { q.family = family; return; }
        vk->queues.push_back({ queue, family });
    }

    VKAPI_ATTR void VKAPI_CALL GetDeviceQueue(VkDevice device, uint32_t family, uint32_t index, VkQueue* queue)
    {
        DeviceData data = Lookup(g_devices, Key(device));
        if (!data.vk || !data.vk->fn.GetDeviceQueue) return;
        data.vk->fn.GetDeviceQueue(device, family, index, queue);
        if (queue) RememberQueue(data.vk, *queue, family);
    }

    VKAPI_ATTR void VKAPI_CALL GetDeviceQueue2(VkDevice device, const VkDeviceQueueInfo2* info, VkQueue* queue)
    {
        DeviceData data = Lookup(g_devices, Key(device));
        if (!data.vk || !data.vk->fn.GetDeviceQueue2) return;
        data.vk->fn.GetDeviceQueue2(device, info, queue);
        if (queue && info) RememberQueue(data.vk, *queue, info->queueFamilyIndex);
    }

    VKAPI_ATTR VkResult VKAPI_CALL CreateDevice(VkPhysicalDevice physical, const VkDeviceCreateInfo* info, const VkAllocationCallbacks* alloc, VkDevice* device)
    {
        auto* link = (VkLayerDeviceCreateInfo*)info->pNext;
        while (link && !(link->sType == VK_STRUCTURE_TYPE_LOADER_DEVICE_CREATE_INFO && link->function == VK_LAYER_LINK_INFO))
            link = (VkLayerDeviceCreateInfo*)link->pNext;
        if (!link || !link->u.pLayerInfo) return VK_ERROR_INITIALIZATION_FAILED;

        // Il loader lascia anche la funzione con cui un layer gli presenta gli oggetti che crea.
        PFN_vkSetDeviceLoaderData setLoaderData = nullptr;
        for (auto* p = (VkLayerDeviceCreateInfo*)info->pNext; p; p = (VkLayerDeviceCreateInfo*)p->pNext)
            if (p->sType == VK_STRUCTURE_TYPE_LOADER_DEVICE_CREATE_INFO && p->function == VK_LOADER_DATA_CALLBACK)
                setLoaderData = p->u.pfnSetDeviceLoaderData;

        PFN_vkGetInstanceProcAddr nextInstance = link->u.pLayerInfo->pfnNextGetInstanceProcAddr;
        PFN_vkGetDeviceProcAddr nextDevice = link->u.pLayerInfo->pfnNextGetDeviceProcAddr;
        link->u.pLayerInfo = link->u.pLayerInfo->pNext;

        InstanceData inst = Lookup(g_instances, Key(physical));
        auto create = (PFN_vkCreateDevice)nextInstance(inst.instance, "vkCreateDevice");
        if (!create) return VK_ERROR_INITIALIZATION_FAILED;
        VkResult result = create(physical, info, alloc, device);
        if (result != VK_SUCCESS) return result;

        auto* vk = new VkState();
        vk->device = *device;
        vk->setLoaderData = setLoaderData;
        bool complete = setLoaderData != nullptr;
#define X(name) vk->fn.name = (PFN_vk##name)nextDevice(*device, "vk" #name);
        FF_VK_FUNCS(X)
#undef X
        // Senza una di queste funzioni non si disegna (limitatore e misura restano attivi).
        // Fa eccezione GetDeviceQueue2, che sui device Vulkan 1.0 non esiste.
#define X(name) if (!vk->fn.name && strcmp(#name, "GetDeviceQueue2")) complete = false;
        FF_VK_FUNCS(X)
#undef X

        auto getMemory = (PFN_vkGetPhysicalDeviceMemoryProperties)nextInstance(inst.instance, "vkGetPhysicalDeviceMemoryProperties");
        auto getFamilies = (PFN_vkGetPhysicalDeviceQueueFamilyProperties)nextInstance(inst.instance, "vkGetPhysicalDeviceQueueFamilyProperties");
        if (getMemory && getFamilies)
        {
            getMemory(physical, &vk->memory);
            uint32_t count = 0;
            getFamilies(physical, &count, nullptr);
            vk->families.resize(count);
            if (count) getFamilies(physical, &count, vk->families.data());
        }
        else complete = false;
        vk->complete = complete;

        DeviceData data = {};
        data.key = Key(*device);
        data.nextGetProcAddr = nextDevice;
        data.destroy = (PFN_vkDestroyDevice)nextDevice(*device, "vkDestroyDevice");
        data.present = (PFN_vkQueuePresentKHR)nextDevice(*device, "vkQueuePresentKHR");
        data.vk = vk;
        g_nextDeviceProcAddr = nextDevice;
        if (!Store(g_devices, data))
        {
            // Tabella piena: il device funziona lo stesso, solo senza di noi (GetDeviceProcAddr lo lascia passare).
            delete vk;
            ff::Log("Vulkan: troppi device, questo passa senza layer");
            return VK_SUCCESS;
        }
        ff::Log("Vulkan: device del gioco agganciato (presentazione %s, overlay %s)",
                data.present ? "disponibile" : "assente", complete ? "possibile" : "non possibile");
        return VK_SUCCESS;
    }

    VKAPI_ATTR void VKAPI_CALL DestroyDevice(VkDevice device, const VkAllocationCallbacks* alloc)
    {
        if (!device) return;
        DeviceData data = Lookup(g_devices, Key(device));
        Erase(g_devices, Key(device));
        if (data.vk)
        {
            DestroyDeviceResources(data.vk);
            delete data.vk;
        }
        if (data.destroy) data.destroy(device, alloc);
    }

    PFN_vkVoidFunction DeviceHook(const char* name)
    {
        if (!strcmp(name, "vkDestroyDevice")) return (PFN_vkVoidFunction)DestroyDevice;
        if (!strcmp(name, "vkQueuePresentKHR")) return (PFN_vkVoidFunction)QueuePresent;
        if (!strcmp(name, "vkCreateSwapchainKHR")) return (PFN_vkVoidFunction)CreateSwapchain;
        if (!strcmp(name, "vkDestroySwapchainKHR")) return (PFN_vkVoidFunction)DestroySwapchain;
        if (!strcmp(name, "vkGetDeviceQueue")) return (PFN_vkVoidFunction)GetDeviceQueue;
        if (!strcmp(name, "vkGetDeviceQueue2")) return (PFN_vkVoidFunction)GetDeviceQueue2;
        return nullptr;
    }

    VKAPI_ATTR PFN_vkVoidFunction VKAPI_CALL GetDeviceProcAddr(VkDevice device, const char* name)
    {
        if (!strcmp(name, "vkGetDeviceProcAddr")) return (PFN_vkVoidFunction)GetDeviceProcAddr;
        DeviceData data = device ? Lookup(g_devices, Key(device)) : DeviceData{};
        if (device && !data.nextGetProcAddr)
        {
            // Device che non conosciamo: tutte le sue funzioni vanno dritte al layer successivo.
            PFN_vkGetDeviceProcAddr next = g_nextDeviceProcAddr;
            return next ? next(device, name) : nullptr;
        }
        if (PFN_vkVoidFunction hook = DeviceHook(name)) return hook;
        return data.nextGetProcAddr ? data.nextGetProcAddr(device, name) : nullptr;
    }

    VKAPI_ATTR PFN_vkVoidFunction VKAPI_CALL GetInstanceProcAddr(VkInstance instance, const char* name)
    {
        if (!strcmp(name, "vkGetInstanceProcAddr")) return (PFN_vkVoidFunction)GetInstanceProcAddr;
        if (!strcmp(name, "vkCreateInstance")) return (PFN_vkVoidFunction)CreateInstance;
        InstanceData data = {};
        if (instance)
        {
            data = Lookup(g_instances, Key(instance));
            if (!data.nextGetProcAddr)
            {
                // Istanza che non conosciamo (tabella piena): tutto dritto al layer successivo.
                PFN_vkGetInstanceProcAddr next = g_nextInstanceProcAddr;
                return next ? next(instance, name) : nullptr;
            }
        }
        if (!strcmp(name, "vkDestroyInstance")) return (PFN_vkVoidFunction)DestroyInstance;
        if (!strcmp(name, "vkCreateDevice")) return (PFN_vkVoidFunction)CreateDevice;
        if (!strcmp(name, "vkGetDeviceProcAddr")) return (PFN_vkVoidFunction)GetDeviceProcAddr;
        if (PFN_vkVoidFunction hook = DeviceHook(name)) return hook;
        if (!instance) return nullptr;
        return data.nextGetProcAddr(instance, name);
    }
}

// Elenchi dell'utente (%APPDATA%\FrameGrip\excluded-processes.txt, lo stesso file letto dall'app):
// [not-games] nomi di processi senza .exe, [background-paths] pezzi di percorso,
// [force-hook] nomi di processi da agganciare anche se sembrano protetti da un anti-cheat.
// "exe" è già in minuscolo.
struct UserLists
{
    bool excluded = false;    // in [not-games] o [background-paths]
    bool forceHook = false;   // in [force-hook]
};

static UserLists ReadUserLists(const wchar_t* exe)
{
    UserLists result;
    wchar_t dir[MAX_PATH] = {};
    DWORD n = GetEnvironmentVariableW(L"APPDATA", dir, MAX_PATH);
    if (n == 0 || n >= MAX_PATH - 64) return result;
    std::wstring path = std::wstring(dir) + L"\\FrameGrip\\excluded-processes.txt";
    HANDLE f = CreateFileW(path.c_str(), GENERIC_READ, FILE_SHARE_READ | FILE_SHARE_WRITE, nullptr, OPEN_EXISTING, 0, nullptr);
    if (f == INVALID_HANDLE_VALUE) return result;
    char buf[16384];
    DWORD got = 0;
    BOOL ok = ReadFile(f, buf, sizeof(buf) - 1, &got, nullptr);
    CloseHandle(f);
    if (!ok || got == 0) return result;
    buf[got] = 0;
    int wlen = MultiByteToWideChar(CP_UTF8, 0, buf, (int)got, nullptr, 0);
    std::wstring text(wlen, L'\0');
    MultiByteToWideChar(CP_UTF8, 0, buf, (int)got, text.data(), wlen);
    for (auto& c : text) c = towlower(c);
    int section = 0;   // 1 = [not-games], 2 = [background-paths], 3 = [force-hook]
    size_t pos = 0;
    while (pos < text.size())
    {
        size_t end = text.find(L'\n', pos);
        if (end == std::wstring::npos) end = text.size();
        std::wstring line = text.substr(pos, end - pos);
        pos = end + 1;
        while (!line.empty() && (line.back() == L'\r' || line.back() == L' ' || line.back() == L'\t')) line.pop_back();
        size_t a = line.find_first_not_of(L" \t\xFEFF");
        if (a == std::wstring::npos) continue;
        line = line.substr(a);
        if (line[0] == L'#') continue;
        if (line[0] == L'[')
        {
            section = line == L"[not-games]" ? 1 : line == L"[background-paths]" ? 2 : line == L"[force-hook]" ? 3 : 0;
            continue;
        }
        if (section == 1 || section == 3)
        {
            if (line.size() < 4 || line.compare(line.size() - 4, 4, L".exe") != 0) line += L".exe";
            if (wcsstr(exe, (L"\\" + line).c_str()))
            {
                if (section == 1) result.excluded = true;
                else result.forceHook = true;
            }
        }
        else if (section == 2 && wcsstr(exe, line.c_str())) result.excluded = true;
    }
    return result;
}

// Strumenti di contorno che usano Vulkan ma non sono giochi (Steam, launcher, browser, registratori...).
static bool NotAGame(const wchar_t* exe)
{
    static const wchar_t* const kNotGames[] = {
        L"\\steam.exe", L"\\steamwebhelper.exe", L"\\steamsysinfo.exe", L"\\vulkandriverquery",
        L"\\gldriverquery", L"\\steamservice.exe", L"\\framegrip.exe", L"\\epicgameslauncher.exe", L"\\galaxyclient",
        L"\\battle.net", L"\\chrome.exe", L"\\msedge.exe", L"\\msedgewebview2.exe", L"\\firefox.exe", L"\\discord.exe",
        L"\\obs64.exe", L"\\windhawk", L"\\cpuz", L"\\hwinfo",
        // launcher
        L"\\riotclientservices.exe", L"\\riotclientux", L"\\eadesktop.exe", L"\\eabackgroundservice.exe",
        L"\\origin.exe", L"\\qtwebengineprocess.exe", L"\\rockstar games\\launcher\\", L"\\socialclubhelper.exe",
        L"\\upc.exe", L"\\ubisoftconnect.exe", L"\\uplaywebcore.exe", L"\\ubisoftgamelauncher",
    };
    for (const wchar_t* name : kNotGames)
        if (wcsstr(exe, name)) return true;
    // Processi figli di Chromium / CEF (pagine dei launcher, browser integrati): hanno "--type=" sulla riga
    // di comando. Il gioco che ospita CEF (processo principale) non ce l'ha.
    const wchar_t* cmd = GetCommandLineW();
    if (cmd && wcsstr(cmd, L"--type=") && (GetModuleHandleW(L"libcef.dll") || wcsstr(cmd, L"--type=gpu-process")))
        return true;
    return false;
}

// Il gioco sembra protetto da un anti-cheat? Allora il layer si rifiuta: entrare in quei processi può
// costare un ban o il blocco del gioco. Restituisce il motivo (per il diario) o nullptr.
// "exe" è il percorso completo in minuscolo. Tutto l'elenco è qui.
static const wchar_t* ProtectedByAntiCheat(const wchar_t* exe, wchar_t* why, size_t whyLen)
{
    // 1. Giochi noti con anti-cheat che non guardano la cartella (VAC, Ricochet, Vanguard...): nome dell'exe.
    static const wchar_t* const kExePrefixes[] = {
        L"cs2.exe", L"csgo.exe", L"dota2.exe", L"project8.exe", L"deadlock.exe", L"valorant", L"r5apex",
        L"cod.exe", L"cod2", L"blackops", L"modernwarfare", L"fortniteclient", L"tslgame",
        L"league of legends.exe", L"destiny2.exe", L"rainbowsix", L"eldenring.exe", L"apex_legends",
    };
    const wchar_t* name = wcsrchr(exe, L'\\');
    name = name ? name + 1 : exe;
    for (const wchar_t* p : kExePrefixes)
        if (wcsncmp(name, p, wcslen(p)) == 0) { swprintf_s(why, whyLen, L"gioco noto (%s)", name); return why; }

    // 2. Moduli anti-cheat già caricati nel processo.
    static const wchar_t* const kModulePrefixes[] = {
        L"easyanticheat", L"eaanticheat", L"eac_", L"beclient", L"battleye", L"bedaisy", L"vgk", L"vgc.",
        L"mhyprot", L"mhypbase", L"gameguard", L"npggnt", L"xigncode", L"x3.xem", L"anticheat",
    };
    HANDLE snap = CreateToolhelp32Snapshot(TH32CS_SNAPMODULE, 0);
    if (snap != INVALID_HANDLE_VALUE)
    {
        MODULEENTRY32W me = { sizeof(me) };
        bool found = false;
        for (BOOL more = Module32FirstW(snap, &me); more && !found; more = Module32NextW(snap, &me))
        {
            _wcslwr_s(me.szModule);
            for (const wchar_t* p : kModulePrefixes)
                if (wcsncmp(me.szModule, p, wcslen(p)) == 0) { found = true; break; }
            if (found) swprintf_s(why, whyLen, L"modulo %s", me.szModule);
        }
        CloseHandle(snap);
        if (found) return why;
    }

    // 3. File o cartelle dell'anti-cheat accanto all'exe, o una / due cartelle più su.
    static const wchar_t* const kMarkers[] = {
        L"EasyAntiCheat", L"EasyAntiCheat_EOS", L"BattlEye", L"start_protected_game.exe",
        L"EAAntiCheat.GameServiceLauncher.exe", L"BEService*.exe", L"EasyAntiCheat_launcher.exe",
    };
    wchar_t dir[MAX_PATH];
    if (wcscpy_s(dir, exe) != 0) return nullptr;
    for (int level = 0; level < 3; level++)
    {
        wchar_t* slash = wcsrchr(dir, L'\\');
        if (!slash || slash == dir) break;
        *slash = 0;   // dir = cartella dell'exe, poi quella sopra, poi quella ancora sopra
        if (wcslen(dir) < 3) break;   // mai la radice del disco (es. "c:")
        for (const wchar_t* m : kMarkers)
        {
            wchar_t path[MAX_PATH + 64];
            swprintf_s(path, L"%s\\%s", dir, m);
            bool hit;
            if (wcschr(m, L'*'))
            {
                WIN32_FIND_DATAW fd;
                HANDLE h = FindFirstFileExW(path, FindExInfoBasic, &fd, FindExSearchNameMatch, nullptr, 0);
                hit = h != INVALID_HANDLE_VALUE;
                if (hit) FindClose(h);
            }
            else hit = GetFileAttributesW(path) != INVALID_FILE_ATTRIBUTES;
            if (hit) { swprintf_s(why, whyLen, L"%s", path); return why; }
        }
    }
    return nullptr;
}

// Punto d'ingresso che il loader Vulkan chiama quando carica il layer.
// Qui non si avvia nulla e non si tiene nulla: se il layer viene rifiutato il loader scarica la DLL
// subito dopo, e deve poterlo fare senza rischi (DllMain in modo layer non avvia thread né agganci).
extern "C" VKAPI_ATTR VkResult VKAPI_CALL vkNegotiateLoaderLayerInterfaceVersion(VkNegotiateLayerInterface* iface)
{
    if (!iface || iface->sType != LAYER_NEGOTIATE_INTERFACE_STRUCT) return VK_ERROR_INITIALIZATION_FAILED;

    // Il layer viene offerto a ogni programma che usa Vulkan, non solo ai giochi: negli strumenti
    // di contorno (Steam, i suoi controlli della scheda video, i launcher, e quelli che l'utente ha
    // escluso) ci si rifiuta subito, così il loader ci scarta e il programma resta com'era.
    wchar_t exe[MAX_PATH] = {};
    GetModuleFileNameW(nullptr, exe, MAX_PATH);
    _wcslwr_s(exe);
    if (NotAGame(exe)) return VK_ERROR_INITIALIZATION_FAILED;
    UserLists lists = ReadUserLists(exe);
    if (lists.excluded) return VK_ERROR_INITIALIZATION_FAILED;
    if (!lists.forceHook)
    {
        wchar_t why[MAX_PATH + 96];
        if (ProtectedByAntiCheat(exe, why, sizeof(why) / sizeof(why[0])))
        {
            ff::Log("layer Vulkan rifiutato: anti-cheat (%ls). Per forzarlo: [force-hook] in excluded-processes.txt", why);
            return VK_ERROR_INITIALIZATION_FAILED;
        }
    }

    if (iface->loaderLayerInterfaceVersion > 2) iface->loaderLayerInterfaceVersion = 2;
    iface->pfnGetInstanceProcAddr = GetInstanceProcAddr;
    iface->pfnGetDeviceProcAddr = GetDeviceProcAddr;
    iface->pfnGetPhysicalDeviceProcAddr = nullptr;
    return VK_SUCCESS;
}
