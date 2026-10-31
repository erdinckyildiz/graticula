"""The networks grapy's deep learning tools train — ADR-248. torchvision's published architectures, and the few the
papers define that torchvision does not carry, written from those papers. Every network is built with random weights:
grapy downloads nothing (a user tool has no network), and torchvision's pretrained weights carry terms of their own
(ADR-248 §5.4). Imported only by a tool that runs a model, so ``import grapy`` never imports torch.

=============================  ===========================================================================================
Train Deep Learning Model type  Network and source
=============================  ===========================================================================================
FASTERRCNN                      Faster R-CNN with a ResNet FPN backbone (Ren et al. 2015, NeurIPS; Lin et al. 2017, CVPR)
RETINANET                       RetinaNet (Lin et al. 2017, ICCV), ResNet FPN, P3–P7
MASKRCNN                        Mask R-CNN (He et al. 2017, ICCV), ResNet FPN
SSD                             SSD300 with its VGG-16 base (Liu et al. 2016, ECCV)
UNET                            U-Net (Ronneberger et al. 2015, MICCAI) with a ResNet encoder (He et al. 2016, CVPR)
DEEPLAB                         DeepLabv3 (Chen et al. 2017, arXiv:1706.05587), ResNet, atrous for the bottleneck ResNets
PSPNET                          PSPNet's pyramid pooling module, bins 1, 2, 3, 6 (Zhao et al. 2017, CVPR)
FEATURE_CLASSIFIER              torchvision's classifier of the backbone (ResNet, ResNeXt, Wide ResNet, VGG, DenseNet,
                                MobileNetV2)
CHANGEDETECTOR                  a siamese ResNet encoder whose features' absolute differences feed a U-Net decoder — FC-Siam-
                                diff (Daudt, Le Saux and Boulch 2018, ICIP) on a ResNet encoder
PIX2PIX                         pix2pix: a U-Net generator and a 70×70 PatchGAN (Isola et al. 2017, CVPR)
CYCLEGAN                        CycleGAN: two ResNet generators of six blocks and two PatchGANs, least-squares adversarial
                                loss and a cycle loss of weight 10 (Zhu et al. 2017, ICCV; Johnson et al. 2016)
=============================  ===========================================================================================

TrainUsingAutoDL also searches FCOS (Tian et al. 2019, ICCV) and FCN (Long et al. 2015, CVPR), which torchvision holds.
"""
from __future__ import annotations

import functools

from . import _deeplearning as dl

RESNETS = {"RESNET18": "resnet18", "RESNET34": "resnet34", "RESNET50": "resnet50", "RESNET101": "resnet101",
           "RESNET152": "resnet152", "RESNEXT50": "resnext50_32x4d", "RESNEXT50_32X4D": "resnext50_32x4d",
           "WIDE_RESNET50": "wide_resnet50_2", "WIDE_RESNET50_2": "wide_resnet50_2"}
CLASSIFIERS = dict(RESNETS, **{"VGG11": "vgg11", "VGG11_BN": "vgg11_bn", "VGG13": "vgg13", "VGG13_BN": "vgg13_bn",
                               "VGG16": "vgg16", "VGG16_BN": "vgg16_bn", "VGG19": "vgg19", "VGG19_BN": "vgg19_bn",
                               "DENSENET121": "densenet121", "DENSENET161": "densenet161", "DENSENET169": "densenet169",
                               "DENSENET201": "densenet201", "MOBILENET_V2": "mobilenet_v2"})
NOT_BACKBONES = {
    "DARKNET53": "Darknet-53 is YOLOv3's backbone and torchvision does not carry it",
    "REID_V1": "it is the re-identification network of Esri's Deep Sort model, which Esri does not publish",
    "REID_V2": "it is the re-identification network of Esri's Deep Sort model, which Esri does not publish",
    "VIT_B": "it is the Segment Anything image encoder SAMLoRA fine-tunes, a model type grapy refuses",
    "VIT_L": "it is the Segment Anything image encoder SAMLoRA fine-tunes, a model type grapy refuses",
    "VIT_H": "it is the Segment Anything image encoder SAMLoRA fine-tunes, a model type grapy refuses",
    "TERRAMIND_BASE": "TerraMind is a foundation model used through its pretrained weights, which grapy does not carry",
    "TERRAMIND_LARGE": "TerraMind is a foundation model used through its pretrained weights, which grapy does not carry",
    "DOFA_BASE": "DOFA is a foundation model used through its pretrained weights, which grapy does not carry",
    "DOFA_LARGE": "DOFA is a foundation model used through its pretrained weights, which grapy does not carry",
    "CLAY_LARGE": "Clay is a foundation model used through its pretrained weights, which grapy does not carry",
    "SR3": "it is the diffusion backbone of Super Resolution, a model type grapy refuses",
    "SR3_UVIT": "it is the diffusion backbone of Super Resolution, a model type grapy refuses",
    "1.40625DEG": "it is a ClimaX resolution, a model type grapy refuses",
    "5.625DEG": "it is a ClimaX resolution, a model type grapy refuses",
}


def backbone_reason(name):
    upper = str(name).upper()
    if upper.startswith("TIMM:"):
        return "timm's backbones need the timm package, which is not among grapy's libraries"
    return NOT_BACKBONES.get(upper)


@functools.lru_cache(maxsize=None)
def nets():
    """The network classes, defined once torch is imported."""
    torch = dl.framework()
    torchvision = dl.vision()
    nn = torch.nn
    F = torch.nn.functional

    class Encoder(nn.Module):
        """A torchvision ResNet's stages as an encoder: features at strides 2, 4, 8, 16 and 32 (8 for the last two
        with ``dilated``, the atrous form DeepLabv3 uses)."""

        def __init__(self, backbone, bands, dilated=False, scheme="RANDOM"):
            super().__init__()
            builder = getattr(torchvision.models, RESNETS[backbone])
            bottleneck = RESNETS[backbone] not in ("resnet18", "resnet34")
            kwargs = {"replace_stride_with_dilation": [False, True, True]} if dilated and bottleneck else {}
            net = builder(weights=None, **kwargs)
            net.conv1 = dl.adapt_first_convolution(net.conv1, bands, scheme)
            self.stem = nn.Sequential(net.conv1, net.bn1, net.relu)
            self.pool = net.maxpool
            self.layer1, self.layer2, self.layer3, self.layer4 = net.layer1, net.layer2, net.layer3, net.layer4
            expansion = 4 if bottleneck else 1
            self.channels = [64, 64 * expansion, 128 * expansion, 256 * expansion, 512 * expansion]

        def forward(self, x):
            x0 = self.stem(x)
            x1 = self.layer1(self.pool(x0))
            x2 = self.layer2(x1)
            x3 = self.layer3(x2)
            x4 = self.layer4(x3)
            return [x0, x1, x2, x3, x4]

    def block(cin, cout):
        return nn.Sequential(nn.Conv2d(cin, cout, 3, padding=1, bias=False), nn.BatchNorm2d(cout), nn.ReLU(inplace=True),
                             nn.Conv2d(cout, cout, 3, padding=1, bias=False), nn.BatchNorm2d(cout), nn.ReLU(inplace=True))

    class Decoder(nn.Module):
        """U-Net's expanding path (Ronneberger et al. 2015): upsample, concatenate the skip, two 3×3 convolutions;
        then up to the input's size and a 1×1 convolution to the classes."""

        def __init__(self, channels, classes, widths=(256, 128, 64, 32)):
            super().__init__()
            c = channels
            self.up = nn.ModuleList()
            cin = c[4]
            for skip, width in zip((c[3], c[2], c[1], c[0]), widths):
                self.up.append(block(cin + skip, width))
                cin = width
            self.last = nn.Sequential(block(cin, 16), nn.Conv2d(16, classes, 1))

        def forward(self, features, size):
            x = features[4]
            for skip, conv in zip((features[3], features[2], features[1], features[0]), self.up):
                x = F.interpolate(x, size=skip.shape[-2:], mode="bilinear", align_corners=False)
                x = conv(torch.cat([x, skip], 1))
            x = F.interpolate(x, size=size, mode="bilinear", align_corners=False)
            return self.last(x)

    class UNet(nn.Module):
        def __init__(self, backbone, bands, classes, scheme="RANDOM"):
            super().__init__()
            self.backbone = Encoder(backbone, bands, scheme=scheme)
            self.decoder = Decoder(self.backbone.channels, classes)

        def forward(self, x):
            return self.decoder(self.backbone(x), x.shape[-2:])

    class Pyramid(nn.Module):
        """PSPNet's pyramid pooling module (Zhao et al. 2017): average pooling to 1, 2, 3 and 6 bins, a 1×1
        convolution to a quarter of the channels each, upsampled and concatenated with the features."""

        def __init__(self, cin, classes, bins=(1, 2, 3, 6)):
            super().__init__()
            self.stages = nn.ModuleList(nn.Sequential(nn.AdaptiveAvgPool2d(b), nn.Conv2d(cin, cin // 4, 1, bias=False),
                                                      nn.BatchNorm2d(cin // 4), nn.ReLU(inplace=True)) for b in bins)
            self.last = nn.Sequential(nn.Conv2d(cin + len(bins) * (cin // 4), 512, 3, padding=1, bias=False),
                                      nn.BatchNorm2d(512), nn.ReLU(inplace=True), nn.Dropout2d(0.1),
                                      nn.Conv2d(512, classes, 1))

        def forward(self, x):
            size = x.shape[-2:]
            pooled = [F.interpolate(s(x), size=size, mode="bilinear", align_corners=False) for s in self.stages]
            return self.last(torch.cat([x] + pooled, 1))

    class Segmenter(nn.Module):
        """A head on the encoder's last features, upsampled to the input: DeepLabv3's ASPP head, PSPNet's pyramid
        pooling or FCN's head."""

        def __init__(self, backbone, bands, classes, head, scheme="RANDOM"):
            super().__init__()
            self.backbone = Encoder(backbone, bands, dilated=head in ("DEEPLAB", "PSPNET"), scheme=scheme)
            c = self.backbone.channels[4]
            from torchvision.models.segmentation.deeplabv3 import DeepLabHead
            from torchvision.models.segmentation.fcn import FCNHead
            self.head = {"DEEPLAB": lambda: DeepLabHead(c, classes), "PSPNET": lambda: Pyramid(c, classes),
                         "FCN": lambda: FCNHead(c, classes)}[head]()

        def forward(self, x):
            out = self.head(self.backbone(x)[4])
            return F.interpolate(out, size=x.shape[-2:], mode="bilinear", align_corners=False)

    class Siamese(nn.Module):
        """FC-Siam-diff (Daudt, Le Saux and Boulch 2018) on a ResNet encoder: both images through one encoder, and the
        absolute difference of their features at each stride into a U-Net decoder."""

        def __init__(self, backbone, bands, classes, scheme="RANDOM"):
            super().__init__()
            self.backbone = Encoder(backbone, bands, scheme=scheme)
            self.decoder = Decoder(self.backbone.channels, classes)

        def forward(self, before, after):
            a, b = self.backbone(before), self.backbone(after)
            return self.decoder([torch.abs(x - y) for x, y in zip(a, b)], before.shape[-2:])

    class UnetGenerator(nn.Module):
        """pix2pix's U-Net generator (Isola et al. 2017, appendix 6.1.1): C64-C128-C256-C512… down to 1×1 (at most
        eight levels), LeakyReLU 0.2 and no batch norm on the first; the decoder mirrors it with skips, dropout 0.5 on
        its first three levels, and a tanh."""

        def __init__(self, bands_in, bands_out, size):
            super().__init__()
            depth = max(1, min(8, int(size).bit_length() - 1))
            widths = [min(64 * 2 ** k, 512) for k in range(depth)]
            self.down = nn.ModuleList()
            cin = bands_in
            for k, w in enumerate(widths):
                layers = [nn.Conv2d(cin, w, 4, 2, 1, bias=False)]
                if 0 < k < depth - 1:
                    layers.append(nn.BatchNorm2d(w))
                self.down.append(nn.Sequential(*layers))
                cin = w
            self.upward = nn.ModuleList()
            for k in range(depth - 1, 0, -1):
                cin = widths[k] if k == depth - 1 else widths[k] * 2
                layers = [nn.ReLU(True), nn.ConvTranspose2d(cin, widths[k - 1], 4, 2, 1, bias=False),
                          nn.BatchNorm2d(widths[k - 1])]
                if depth - 1 - k < 3:
                    layers.append(nn.Dropout(0.5))
                self.upward.append(nn.Sequential(*layers))
            self.last = nn.Sequential(nn.ReLU(True), nn.ConvTranspose2d(widths[0] * 2 if depth > 1 else widths[0],
                                                                        bands_out, 4, 2, 1), nn.Tanh())

        def forward(self, x):
            skips = []
            for k, d in enumerate(self.down):
                x = d(x if k == 0 else F.leaky_relu(x, 0.2))
                skips.append(x)
            for k, u in enumerate(self.upward):
                x = torch.cat([u(x), skips[-2 - k]], 1)
            return self.last(x)

    class PatchGAN(nn.Module):
        """The 70×70 PatchGAN discriminator (Isola et al. 2017, appendix 6.1.2): C64-C128-C256-C512, the last of
        stride 1, then a 1-channel convolution; batch norm (pix2pix) or instance norm (CycleGAN)."""

        def __init__(self, bands, norm="batch"):
            super().__init__()
            Norm = nn.BatchNorm2d if norm == "batch" else nn.InstanceNorm2d
            self.net = nn.Sequential(
                nn.Conv2d(bands, 64, 4, 2, 1), nn.LeakyReLU(0.2, True),
                nn.Conv2d(64, 128, 4, 2, 1, bias=False), Norm(128), nn.LeakyReLU(0.2, True),
                nn.Conv2d(128, 256, 4, 2, 1, bias=False), Norm(256), nn.LeakyReLU(0.2, True),
                nn.Conv2d(256, 512, 4, 1, 1, bias=False), Norm(512), nn.LeakyReLU(0.2, True),
                nn.Conv2d(512, 1, 4, 1, 1))

        def forward(self, x):
            return self.net(x)

    class Residual(nn.Module):
        def __init__(self, c):
            super().__init__()
            self.net = nn.Sequential(nn.ReflectionPad2d(1), nn.Conv2d(c, c, 3), nn.InstanceNorm2d(c), nn.ReLU(True),
                                     nn.ReflectionPad2d(1), nn.Conv2d(c, c, 3), nn.InstanceNorm2d(c))

        def forward(self, x):
            return x + self.net(x)

    class ResnetGenerator(nn.Module):
        """CycleGAN's generator (Zhu et al. 2017, appendix 7.2, after Johnson et al. 2016): c7s1-64, d128, d256,
        six R256, u128, u64, c7s1-k and a tanh."""

        def __init__(self, bands_in, bands_out, blocks=6):
            super().__init__()
            layers = [nn.ReflectionPad2d(3), nn.Conv2d(bands_in, 64, 7), nn.InstanceNorm2d(64), nn.ReLU(True),
                      nn.Conv2d(64, 128, 3, 2, 1), nn.InstanceNorm2d(128), nn.ReLU(True),
                      nn.Conv2d(128, 256, 3, 2, 1), nn.InstanceNorm2d(256), nn.ReLU(True)]
            layers += [Residual(256) for _ in range(blocks)]
            layers += [nn.ConvTranspose2d(256, 128, 3, 2, 1, output_padding=1), nn.InstanceNorm2d(128), nn.ReLU(True),
                       nn.ConvTranspose2d(128, 64, 3, 2, 1, output_padding=1), nn.InstanceNorm2d(64), nn.ReLU(True),
                       nn.ReflectionPad2d(3), nn.Conv2d(64, bands_out, 7), nn.Tanh()]
            self.net = nn.Sequential(*layers)

        def forward(self, x):
            return self.net(x)

    class Pix2Pix(nn.Module):
        def __init__(self, bands_in, bands_out, size):
            super().__init__()
            self.generator = UnetGenerator(bands_in, bands_out, size)
            self.discriminator = PatchGAN(bands_in + bands_out)

        def forward(self, x):
            return self.generator(x)

    class CycleGAN(nn.Module):
        def __init__(self, bands_a, bands_b):
            super().__init__()
            self.a_to_b, self.b_to_a = ResnetGenerator(bands_a, bands_b), ResnetGenerator(bands_b, bands_a)
            self.discriminator_a, self.discriminator_b = PatchGAN(bands_a, "instance"), PatchGAN(bands_b, "instance")

        def forward(self, x, direction="AtoB"):
            return (self.a_to_b if direction == "AtoB" else self.b_to_a)(x)

    return {"Encoder": Encoder, "UNet": UNet, "Segmenter": Segmenter, "Siamese": Siamese, "Pix2Pix": Pix2Pix,
            "CycleGAN": CycleGAN}


def detector(architecture, backbone, bands, classes, chip, scheme="RANDOM"):
    """A torchvision detector for ``classes`` object classes (and the background), its input normalised by grapy
    before it (so its own transform's mean is 0 and deviation 1) and kept at the chip's size. INFERRED (ADR-248 §5.4):
    the backbones train from random weights with ordinary batch norm, not torchvision's frozen batch norm, which
    assumes pretrained statistics; anchors are scaled to the chip — sizes chip/16 to chip on the five FPN levels for
    a 256-pixel chip as for torchvision's 32 to 512 on 800-pixel images, each level's twice the last."""
    torch = dl.framework()
    torchvision = dl.vision()
    nn = torch.nn
    from torchvision.models.detection import FCOS, FasterRCNN, MaskRCNN, RetinaNet
    from torchvision.models.detection.anchor_utils import AnchorGenerator
    from torchvision.models.detection.backbone_utils import resnet_fpn_backbone
    from torchvision.ops.feature_pyramid_network import LastLevelP6P7
    size = int(max(chip))
    common = {"min_size": size, "max_size": size, "image_mean": [0.0] * bands, "image_std": [1.0] * bands}
    if architecture == "SSD":
        model = torchvision.models.detection.ssd300_vgg16(weights=None, weights_backbone=None, num_classes=classes + 1,
                                                          image_mean=[0.0] * bands, image_std=[1.0] * bands)
        first = model.backbone.features[0]
        model.backbone.features[0] = dl.adapt_first_convolution(first, bands, scheme)
        return model
    sizes = tuple((max(4, size // 2 ** (4 - k)),) for k in range(5))
    if architecture in ("RETINANET", "FCOS"):
        body = resnet_fpn_backbone(backbone_name=RESNETS[backbone], weights=None, norm_layer=nn.BatchNorm2d,
                                   trainable_layers=5, returned_layers=[2, 3, 4], extra_blocks=LastLevelP6P7(256, 256))
        body.body.conv1 = dl.adapt_first_convolution(body.body.conv1, bands, scheme)
        if architecture == "FCOS":
            anchors = AnchorGenerator(sizes=tuple((s[0],) for s in sizes), aspect_ratios=((1.0,),) * 5)
            return FCOS(body, num_classes=classes + 1, anchor_generator=anchors, **common)
        anchors = AnchorGenerator(sizes=tuple((s[0], int(s[0] * 2 ** (1 / 3)), int(s[0] * 2 ** (2 / 3))) for s in sizes),
                                  aspect_ratios=((0.5, 1.0, 2.0),) * 5)
        return RetinaNet(body, num_classes=classes + 1, anchor_generator=anchors, **common)
    body = resnet_fpn_backbone(backbone_name=RESNETS[backbone], weights=None, norm_layer=nn.BatchNorm2d,
                               trainable_layers=5)
    body.body.conv1 = dl.adapt_first_convolution(body.body.conv1, bands, scheme)
    anchors = AnchorGenerator(sizes=sizes, aspect_ratios=((0.5, 1.0, 2.0),) * 5)
    kind = MaskRCNN if architecture == "MASKRCNN" else FasterRCNN
    return kind(body, num_classes=classes + 1, rpn_anchor_generator=anchors, **common)


def classifier(backbone, bands, classes, scheme="RANDOM"):
    torch = dl.framework()
    torchvision = dl.vision()
    name = CLASSIFIERS[backbone]
    model = getattr(torchvision.models, name)(weights=None, num_classes=classes)
    if name.startswith(("resnet", "resnext", "wide")):
        model.conv1 = dl.adapt_first_convolution(model.conv1, bands, scheme)
    elif name.startswith("vgg"):
        model.features[0] = dl.adapt_first_convolution(model.features[0], bands, scheme)
        model.avgpool = torch.nn.AdaptiveAvgPool2d((7, 7))
    elif name.startswith("densenet"):
        model.features.conv0 = dl.adapt_first_convolution(model.features.conv0, bands, scheme)
    else:
        model.features[0][0] = dl.adapt_first_convolution(model.features[0][0], bands, scheme)
    return model


def build(architecture, backbone, bands, classes, chip, bands_out=None, scheme="RANDOM"):
    """The network for an architecture; ``classes`` counts the model's outputs (for pixel and change models, the
    background class 0 among them; for detectors, the object classes)."""
    n = nets()
    if architecture in ("FASTERRCNN", "RETINANET", "MASKRCNN", "SSD", "FCOS"):
        return detector(architecture, backbone, bands, classes, chip, scheme)
    if architecture == "UNET":
        return n["UNet"](backbone, bands, classes, scheme)
    if architecture in ("DEEPLAB", "PSPNET", "FCN"):
        return n["Segmenter"](backbone, bands, classes, architecture, scheme)
    if architecture == "FEATURE_CLASSIFIER":
        return classifier(backbone, bands, classes, scheme)
    if architecture == "CHANGEDETECTOR":
        return n["Siamese"](backbone, bands, classes, scheme)
    if architecture == "PIX2PIX":
        return n["Pix2Pix"](bands, bands_out or bands, int(max(chip)))
    if architecture == "CYCLEGAN":
        return n["CycleGAN"](bands, bands_out or bands)
    raise ValueError(f"'{architecture}' is not a network grapy builds.")
