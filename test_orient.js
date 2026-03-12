const fs = require('fs');

async function test() {
    const imgPath = 'C:\\Users\\QYTH4815\\AppData\\Local\\RobotControllerApp\\Library\\BLACK_REMOTE_CONTROL_banana.png';
    const buf = fs.readFileSync(imgPath);
    const dataUrl = 'data:image/png;base64,' + buf.toString('base64');
    
    const payload = { data: [ { url: dataUrl, meta: { _type: 'gradio.FileData' } }, null, true ] };
    
    const req1 = await fetch('https://viglong-orient-anything-v2.hf.space/gradio_api/call/run_inference', {
        method: 'POST',
        headers: { 'Content-Type': 'application/json' },
        body: JSON.stringify(payload)
    });
    
    const msg = await req1.json();
    console.log('Event ID:', msg.event_id);
    
    const req2 = await fetch('https://viglong-orient-anything-v2.hf.space/gradio_api/call/run_inference/' + msg.event_id, {
        method: 'GET',
        headers: { 'Accept': 'text/event-stream' }
    });
    
    const text = await req2.text();
    console.log('SSE response length:', text.length);
    const parts = text.split('\n');
    let finalData = '';
    for (let i = 0; i < parts.length; i++) {
        if (parts[i].startsWith('event: complete')) {
            finalData = parts[i+1];
            break;
        }
    }
    
    if (finalData) {
        console.log('Final data:', finalData.substring(0, 500) + '...');
        const doc = JSON.parse(finalData.substring(6));
        console.log('Array length:', doc.length);
        console.log('Index 0:', doc[0]);
    } else {
        console.log('No complete event found. SSE:', text);
    }
}
test().catch(console.error);
