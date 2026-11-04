// A virtual HiDPI ("Retina", 2x) display for testing on a CI runner, whose only screen is
// 1x: CoreGraphics' CGVirtualDisplay (macOS 11+, not public; the interface below is the one
// display tools such as DeskPad use). It becomes the main display, so new windows open on it,
// and lasts until this process ends.
// Build: clang -fobjc-arc -framework Foundation -framework CoreGraphics hidpi-display.m -o hidpi-display
// Usage: hidpi-display [width height]   (points; the display has twice as many pixels)
#import <Foundation/Foundation.h>
#import <CoreGraphics/CoreGraphics.h>

@interface CGVirtualDisplayDescriptor : NSObject
@property (retain, nonatomic) dispatch_queue_t queue;
@property (retain, nonatomic) NSString *name;
@property (nonatomic) unsigned int maxPixelsWide;
@property (nonatomic) unsigned int maxPixelsHigh;
@property (nonatomic) CGSize sizeInMillimeters;
@property (nonatomic) unsigned int productID;
@property (nonatomic) unsigned int vendorID;
@property (nonatomic) unsigned int serialNum;
@property (copy, nonatomic) void (^terminationHandler)(id, id);
@end

@interface CGVirtualDisplayMode : NSObject
- (instancetype)initWithWidth:(unsigned int)width height:(unsigned int)height refreshRate:(double)refreshRate;
@end

@interface CGVirtualDisplaySettings : NSObject
@property (retain, nonatomic) NSArray *modes;
@property (nonatomic) unsigned int hiDPI;
@end

@interface CGVirtualDisplay : NSObject
- (instancetype)initWithDescriptor:(CGVirtualDisplayDescriptor *)descriptor;
- (BOOL)applySettings:(CGVirtualDisplaySettings *)settings;
@property (readonly, nonatomic) unsigned int displayID;
@end

int main(int argc, const char *argv[])
{
    @autoreleasepool {
        unsigned int width = argc > 2 ? (unsigned int)atoi(argv[1]) : 1280;
        unsigned int height = argc > 2 ? (unsigned int)atoi(argv[2]) : 800;

        CGVirtualDisplayDescriptor *descriptor = [[NSClassFromString(@"CGVirtualDisplayDescriptor") alloc] init];
        if (descriptor == nil) {
            fprintf(stderr, "CGVirtualDisplay is not available\n");
            return 1;
        }
        descriptor.queue = dispatch_get_main_queue();
        descriptor.name = @"HiDPI test display";
        descriptor.maxPixelsWide = width * 2;
        descriptor.maxPixelsHigh = height * 2;
        descriptor.sizeInMillimeters = CGSizeMake(width * 2 * 25.4 / 220.0, height * 2 * 25.4 / 220.0);
        descriptor.productID = 0x1234;
        descriptor.vendorID = 0x3456;
        descriptor.serialNum = 0x0001;
        descriptor.terminationHandler = ^(id a, id b) { fprintf(stderr, "virtual display terminated\n"); };

        CGVirtualDisplay *display = [[NSClassFromString(@"CGVirtualDisplay") alloc] initWithDescriptor:descriptor];
        if (display == nil || display.displayID == 0) {
            fprintf(stderr, "could not create the virtual display\n");
            return 1;
        }
        CGVirtualDisplaySettings *settings = [[NSClassFromString(@"CGVirtualDisplaySettings") alloc] init];
        settings.hiDPI = 1;
        settings.modes = @[ [[NSClassFromString(@"CGVirtualDisplayMode") alloc] initWithWidth:width height:height refreshRate:60] ];
        if (![display applySettings:settings]) {
            fprintf(stderr, "could not apply the HiDPI mode\n");
            return 1;
        }

        // the main display is the one at the origin: put the virtual one there
        CGDirectDisplayID previousMain = CGMainDisplayID();
        CGDisplayConfigRef config;
        if (CGBeginDisplayConfiguration(&config) == kCGErrorSuccess) {
            CGConfigureDisplayOrigin(config, display.displayID, 0, 0);
            if (previousMain != display.displayID)
                CGConfigureDisplayOrigin(config, previousMain, (int32_t)width, 0);
            CGCompleteDisplayConfiguration(config, kCGConfigureForSession);
        }
        [NSThread sleepForTimeInterval:2];

        CGDirectDisplayID main = CGMainDisplayID();
        CGDisplayModeRef mode = CGDisplayCopyDisplayMode(main);
        printf("HIDPI display %u: main=%s, %zux%zu points, %zux%zu pixels\n", display.displayID,
               main == display.displayID ? "yes" : "no",
               mode ? CGDisplayModeGetWidth(mode) : 0, mode ? CGDisplayModeGetHeight(mode) : 0,
               mode ? CGDisplayModeGetPixelWidth(mode) : 0, mode ? CGDisplayModeGetPixelHeight(mode) : 0);
        fflush(stdout);
        if (mode)
            CGDisplayModeRelease(mode);

        // keep the display until killed
        dispatch_main();
    }
    return 0;
}
