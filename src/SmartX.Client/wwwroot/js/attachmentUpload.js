// Uploads attachments with real progress, straight from the browser's File objects.
//
// Why JavaScript at all, in a Blazor app. HttpClient in Blazor WebAssembly is built on fetch, and fetch
// offers no upload progress events — a progress bar driven from .NET could only animate a guess. Worse,
// sending a file through HttpClient means reading it into the WebAssembly heap first, so a 90 MB log
// would cost 90 MB of browser memory before a byte left the machine. XMLHttpRequest does both jobs
// properly: upload.onprogress reports the bytes actually handed to the network, and a File appended to
// FormData is streamed from disk by the browser without ever being copied into .NET.
//
// The File objects stay here, keyed by an id, and .NET only ever holds their name, size and type.

const files = new Map();
const active = new Map();
let nextId = 0;

// Progress is reported at most this often. Every progress event crossing into .NET costs an interop call
// and a re-render; ten a second is smooth to watch and cheap to run.
const progressIntervalMs = 100;

// Takes the files currently selected in the file input inside a container, and returns their details.
export function capture(container) {
    const input = container?.querySelector('input[type="file"]');
    const captured = [];

    if (!input?.files) {
        return captured;
    }

    for (const file of input.files) {
        const id = `upload-${++nextId}`;
        files.set(id, file);
        captured.push({ id, name: file.name, size: file.size, type: file.type });
    }

    return captured;
}

// Uploads one captured file as a single-file multipart request. Resolves with the HTTP status and response
// body; never rejects, so every outcome — success, rejection, abort, network failure — is a value .NET can show.
export function upload(id, url, progressSink, token) {
    const file = files.get(id);

    return new Promise(resolve => {
        if (!file) {
            resolve({ status: 0, body: '' });
            return;
        }

        const request = new XMLHttpRequest();
        let lastReport = 0;

        const finish = (status, body) => {
            active.delete(id);
            files.delete(id);
            resolve({ status, body });
        };

        request.upload.onprogress = event => {
            const now = performance.now();

            if (event.lengthComputable && (now - lastReport >= progressIntervalMs || event.loaded === event.total)) {
                lastReport = now;
                progressSink.invokeMethodAsync('ReportProgress', id, event.loaded, event.total);
            }
        };

        request.onload = () => finish(request.status, request.responseText);
        request.onerror = () => finish(0, '');
        request.onabort = () => finish(-1, '');

        const form = new FormData();
        form.append('file', file, file.name);

        active.set(id, request);
        request.open('POST', url);

        // The bearer token goes on as a header, as on every other request. XMLHttpRequest is not the app's HttpClient, so nothing
        // adds it for us.
        if (token) {
            request.setRequestHeader('Authorization', 'Bearer ' + token);
        }

        request.send(form);
    });
}

// Fetches a protected file with the bearer token and returns an address for it that an <img> can show. An image tag cannot send an
// Authorization header, so it cannot fetch anything the API protects; this does the fetch and hands the bytes over as a blob.
// Resolves with null on any failure, so a missing thumbnail is a value and not an exception.
export async function objectUrl(url, token) {
    try {
        const response = await fetch(url, { headers: { Authorization: 'Bearer ' + token } });

        return response.ok ? URL.createObjectURL(await response.blob()) : null;
    } catch {
        return null;
    }
}

// Downloads a protected file: the same authenticated fetch, then a save through a temporary link. Resolves with whether it worked.
export async function download(url, token, fileName) {
    const address = await objectUrl(url, token);

    if (!address) {
        return false;
    }

    const link = document.createElement('a');
    link.href = address;
    link.download = fileName;
    document.body.appendChild(link);
    link.click();
    link.remove();
    setTimeout(() => URL.revokeObjectURL(address), 30000);

    return true;
}

// Lets go of an address made by objectUrl, so the browser can free the bytes behind it.
export function revoke(address) {
    URL.revokeObjectURL(address);
}

// Stops an upload in progress. Its promise resolves with status -1.
export function cancel(id) {
    active.get(id)?.abort();
}

// Forgets a captured file that will not be uploaded, so the browser can release it.
export function release(id) {
    files.delete(id);
}
