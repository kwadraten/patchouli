#import "patchouli_macos_fs.h"
#import <Foundation/Foundation.h>

static void copy_string(const char* source, char* destination, size_t destination_len)
{
    if (destination_len == 0)
    {
        return;
    }

    strncpy(destination, source ? source : "", destination_len - 1);
    destination[destination_len - 1] = '\0';
}

static void copy_error(NSError* error, char* err, size_t err_len)
{
    if (err_len == 0)
    {
        return;
    }

    if (error != nil)
    {
        NSString* message = error.localizedDescription;
        copy_string(message.UTF8String, err, err_len);
    }
    else
    {
        copy_string("Unknown error.", err, err_len);
    }
}

int patchouli_resolve_path(const char* path, char* out, size_t out_len, char* err, size_t err_len)
{
    if (path == NULL || out == NULL || out_len == 0 || err == NULL || err_len == 0)
    {
        if (err != NULL && err_len > 0)
        {
            copy_string("Invalid arguments.", err, err_len);
        }
        return -1;
    }

    @autoreleasepool
    {
        NSString* path_string = [NSString stringWithUTF8String:path];
        if (path_string == nil)
        {
            copy_string("Path is not valid UTF-8.", err, err_len);
            return -1;
        }

        NSURL* url = [NSURL fileURLWithPath:path_string];

        // Resolve POSIX symlinks first.
        NSURL* resolved = url.URLByResolvingSymlinksInPath;

        // Then resolve Finder aliases / bookmark files.
        NSError* error = nil;
        NSNumber* is_alias = nil;
        if ([resolved getResourceValue:&is_alias forKey:NSURLIsAliasFileKey error:&error])
        {
            if (is_alias.boolValue)
            {
                NSURL* target = [NSURL URLByResolvingAliasFileAtURL:resolved
                                                            options:NSURLBookmarkResolutionWithoutUI
                                                              error:&error];
                if (target != nil)
                {
                    resolved = target;
                }
                else
                {
                    copy_error(error, err, err_len);
                    return -1;
                }
            }
        }
        else if (error != nil)
        {
            // Reading the alias flag failed; keep the symlink-resolved URL and continue.
        }

        const char* resolved_path = resolved.fileSystemRepresentation;
        if (resolved_path == NULL)
        {
            copy_string("Could not obtain filesystem representation.", err, err_len);
            return -1;
        }

        copy_string(resolved_path, out, out_len);
        return 0;
    }
}

int patchouli_materialize_file(const char* path, char* out, size_t out_len, char* err, size_t err_len,
    unsigned int timeout_ms)
{
    if (path == NULL || out == NULL || out_len == 0 || err == NULL || err_len == 0)
    {
        if (err != NULL && err_len > 0)
        {
            copy_string("Invalid arguments.", err, err_len);
        }
        return -1;
    }

    @autoreleasepool
    {
        NSString* path_string = [NSString stringWithUTF8String:path];
        if (path_string == nil)
        {
            copy_string("Path is not valid UTF-8.", err, err_len);
            return -1;
        }

        NSURL* url = [NSURL fileURLWithPath:path_string];
        NSError* error = nil;

        // Check whether this is an iCloud Drive item.
        NSNumber* is_ubiquitous = nil;
        if (![url getResourceValue:&is_ubiquitous forKey:NSURLIsUbiquitousItemKey error:&error])
        {
            // A non-ubiquitous path can still fail resource-value reads; keep going.
            is_ubiquitous = @NO;
        }

        if (is_ubiquitous.boolValue)
        {
            NSString* downloading_status = nil;
            if ([url getResourceValue:&downloading_status forKey:NSURLUbiquitousItemDownloadingStatusKey error:&error])
            {
                if (![downloading_status isEqualToString:NSURLUbiquitousItemDownloadingStatusCurrent])
                {
                    if (![[NSFileManager defaultManager] startDownloadingUbiquitousItemAtURL:url error:&error])
                    {
                        copy_error(error, err, err_len);
                        return 1;
                    }

                    // If the caller provided a timeout, poll until the file becomes locally
                    // available or the timeout expires. A timeout of 0 means "start the download
                    // and return immediately" so the managed caller can poll with cancellation.
                    if (timeout_ms > 0)
                    {
                        NSTimeInterval start = [NSDate timeIntervalSinceReferenceDate];
                        NSTimeInterval timeout_seconds = timeout_ms / 1000.0;
                        while (([NSDate timeIntervalSinceReferenceDate] - start) < timeout_seconds)
                        {
                            NSString* current_status = nil;
                            if ([url getResourceValue:&current_status
                                               forKey:NSURLUbiquitousItemDownloadingStatusKey
                                                error:nil])
                            {
                                if ([current_status isEqualToString:NSURLUbiquitousItemDownloadingStatusCurrent])
                                {
                                    break;
                                }
                            }

                            // 50 ms polling interval.
                            [NSThread sleepForTimeInterval:0.05];
                        }

                        NSString* final_status = nil;
                        if (![url getResourceValue:&final_status
                                            forKey:NSURLUbiquitousItemDownloadingStatusKey
                                             error:nil] ||
                            ![final_status isEqualToString:NSURLUbiquitousItemDownloadingStatusCurrent])
                        {
                            copy_string("iCloud file is not downloaded and could not be materialized.", err, err_len);
                            return 1;
                        }
                    }
                    else
                    {
                        copy_string("iCloud file is not downloaded and download was started.", err, err_len);
                        return 1;
                    }
                }
            }
            else if (error != nil)
            {
                copy_error(error, err, err_len);
                return -1;
            }
        }

        const char* resolved_path = url.fileSystemRepresentation;
        if (resolved_path == NULL)
        {
            copy_string("Could not obtain filesystem representation.", err, err_len);
            return -1;
        }

        copy_string(resolved_path, out, out_len);
        return 0;
    }
}
