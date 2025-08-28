// Simple SVG-based Charts for OpenHFT Dashboard

// Initialize Candlestick Chart using native SVG
window.initializeCandlestickChart = function(chartId, data) {
    try {
        console.log('🔄 Initializing candlestick chart...', chartId, data);
        
        const container = document.getElementById(chartId);
        if (!container) {
            console.error('❌ Chart container not found:', chartId);
            return;
        }

        // Ensure we have data
        if (!data || data.length === 0) {
            console.log('📊 No data provided, generating sample data');
            data = generateSampleCandlestickData();
        }

        // Clear existing content
        container.innerHTML = '';

        // Chart dimensions
        const width = container.clientWidth || 600;
        const height = container.clientHeight || 300;
        const margin = { top: 20, right: 30, bottom: 40, left: 70 };
        const chartWidth = width - margin.left - margin.right;
        const chartHeight = height - margin.top - margin.bottom;

        // Create SVG
        const svg = document.createElementNS('http://www.w3.org/2000/svg', 'svg');
        svg.setAttribute('width', width);
        svg.setAttribute('height', height);
        svg.style.background = '#f8f9fa';
        svg.style.border = '1px solid #dee2e6';
        svg.style.borderRadius = '12px';
        svg.style.boxShadow = '0 4px 6px rgba(0, 0, 0, 0.1)';

        // Add gradient definitions for enhanced visuals
        const defs = document.createElementNS('http://www.w3.org/2000/svg', 'defs');
        const priceGradient = document.createElementNS('http://www.w3.org/2000/svg', 'linearGradient');
        priceGradient.setAttribute('id', 'priceGradient');
        priceGradient.setAttribute('x1', '0%');
        priceGradient.setAttribute('y1', '0%');
        priceGradient.setAttribute('x2', '0%');
        priceGradient.setAttribute('y2', '100%');
        
        const stop1 = document.createElementNS('http://www.w3.org/2000/svg', 'stop');
        stop1.setAttribute('offset', '0%');
        stop1.setAttribute('stop-color', '#007bff');
        stop1.setAttribute('stop-opacity', '0.6');
        
        const stop2 = document.createElementNS('http://www.w3.org/2000/svg', 'stop');
        stop2.setAttribute('offset', '100%');
        stop2.setAttribute('stop-color', '#007bff');
        stop2.setAttribute('stop-opacity', '0.1');
        
        priceGradient.appendChild(stop1);
        priceGradient.appendChild(stop2);
        defs.appendChild(priceGradient);
        svg.appendChild(defs);

        // Calculate price range
        const prices = data.flatMap(d => [parseFloat(d.close), parseFloat(d.high), parseFloat(d.low), parseFloat(d.open)]);
        const minPrice = Math.min(...prices) * 0.999;
        const maxPrice = Math.max(...prices) * 1.001;
        const priceRange = maxPrice - minPrice;

        // Create price scale
        const scaleY = (price) => margin.top + ((maxPrice - price) / priceRange) * chartHeight;
        const scaleX = (index) => margin.left + (index / (data.length - 1)) * chartWidth;

        // Draw grid lines
        for (let i = 0; i < 5; i++) {
            const y = margin.top + (i / 4) * chartHeight;
            const price = maxPrice - (i / 4) * priceRange;
            
            // Horizontal grid line
            const line = document.createElementNS('http://www.w3.org/2000/svg', 'line');
            line.setAttribute('x1', margin.left);
            line.setAttribute('y1', y);
            line.setAttribute('x2', margin.left + chartWidth);
            line.setAttribute('y2', y);
            line.setAttribute('stroke', '#e9ecef');
            line.setAttribute('stroke-width', '1');
            svg.appendChild(line);

            // Price label
            const text = document.createElementNS('http://www.w3.org/2000/svg', 'text');
            text.setAttribute('x', margin.left - 10);
            text.setAttribute('y', y + 4);
            text.setAttribute('text-anchor', 'end');
            text.setAttribute('font-family', 'Arial, sans-serif');
            text.setAttribute('font-size', '12');
            text.setAttribute('fill', '#495057');
            text.textContent = '$' + price.toFixed(2);
            svg.appendChild(text);
        }

        // Draw enhanced price chart with area fill
        let pathData = '';
        let areaData = '';
        data.forEach((item, index) => {
            const x = scaleX(index);
            const y = scaleY(parseFloat(item.close));
            
            if (index === 0) {
                pathData += `M ${x} ${y}`;
                areaData += `M ${x} ${margin.top + chartHeight} L ${x} ${y}`;
            } else {
                pathData += ` L ${x} ${y}`;
                areaData += ` L ${x} ${y}`;
            }
        });
        
        // Close the area path
        areaData += ` L ${scaleX(data.length - 1)} ${margin.top + chartHeight} Z`;

        // Create area fill
        const area = document.createElementNS('http://www.w3.org/2000/svg', 'path');
        area.setAttribute('d', areaData);
        area.setAttribute('fill', 'url(#priceGradient)');
        svg.appendChild(area);

        // Create enhanced price line
        const path = document.createElementNS('http://www.w3.org/2000/svg', 'path');
        path.setAttribute('d', pathData);
        path.setAttribute('fill', 'none');
        path.setAttribute('stroke', '#007bff');
        path.setAttribute('stroke-width', '3');
        path.setAttribute('stroke-linecap', 'round');
        path.setAttribute('stroke-linejoin', 'round');
        svg.appendChild(path);

        // Draw enhanced price points
        data.forEach((item, index) => {
            const x = scaleX(index);
            const y = scaleY(parseFloat(item.close));
            
            const circle = document.createElementNS('http://www.w3.org/2000/svg', 'circle');
            circle.setAttribute('cx', x);
            circle.setAttribute('cy', y);
            circle.setAttribute('r', '4');
            circle.setAttribute('fill', '#007bff');
            circle.setAttribute('stroke', '#ffffff');
            circle.setAttribute('stroke-width', '2');
            circle.style.filter = 'drop-shadow(0 2px 4px rgba(0,0,0,0.2))';
            
            // Enhanced tooltip
            circle.addEventListener('mouseenter', function(e) {
                circle.setAttribute('r', '6');
                const price = parseFloat(item.close).toFixed(4);
                showTooltip(e, `Price: $${price}\nTime: ${item.time}`);
            });
            circle.addEventListener('mouseleave', function() {
                circle.setAttribute('r', '4');
                hideTooltip();
            });
            
            svg.appendChild(circle);
        });

        // Add time labels on X-axis
        const timeLabels = [0, Math.floor(data.length / 4), Math.floor(data.length / 2), Math.floor(data.length * 3 / 4), data.length - 1];
        timeLabels.forEach(index => {
            if (index < data.length) {
                const x = scaleX(index);
                const y = margin.top + chartHeight + 20;
                
                const timeText = document.createElementNS('http://www.w3.org/2000/svg', 'text');
                timeText.setAttribute('x', x);
                timeText.setAttribute('y', y);
                timeText.setAttribute('text-anchor', 'middle');
                timeText.setAttribute('font-family', 'Arial, sans-serif');
                timeText.setAttribute('font-size', '10');
                timeText.setAttribute('fill', '#6c757d');
                timeText.textContent = data[index].time;
                svg.appendChild(timeText);
            }
        });

        // Add title
        const title = document.createElementNS('http://www.w3.org/2000/svg', 'text');
        title.setAttribute('x', width / 2);
        title.setAttribute('y', 15);
        title.setAttribute('text-anchor', 'middle');
        title.setAttribute('font-family', 'Arial, sans-serif');
        title.setAttribute('font-size', '14');
        title.setAttribute('font-weight', 'bold');
        title.setAttribute('fill', '#212529');
        title.textContent = 'Price Chart (USDT)';
        svg.appendChild(title);

        container.appendChild(svg);
        console.log('✅ Candlestick chart created successfully');

    } catch (error) {
        console.error('❌ Error creating candlestick chart:', error);
    }
};

// Initialize Volume Chart using native SVG
window.initializeVolumeChart = function(chartId, data) {
    try {
        console.log('🔄 Initializing volume chart...', chartId, data);
        
        const container = document.getElementById(chartId);
        if (!container) {
            console.error('❌ Volume chart container not found:', chartId);
            return;
        }

        // Ensure we have data
        if (!data || data.length === 0) {
            console.log('📊 No data provided, generating sample data');
            data = generateSampleVolumeData();
        }

        // Clear existing content
        container.innerHTML = '';

        // Chart dimensions
        const width = container.clientWidth || 600;
        const height = container.clientHeight || 200;
        const margin = { top: 20, right: 30, bottom: 40, left: 70 };
        const chartWidth = width - margin.left - margin.right;
        const chartHeight = height - margin.top - margin.bottom;

        // Create enhanced SVG for volume chart
        const svg = document.createElementNS('http://www.w3.org/2000/svg', 'svg');
        svg.setAttribute('width', width);
        svg.setAttribute('height', height);
        svg.style.background = 'linear-gradient(135deg, #f8f9fa 0%, #e9ecef 100%)';
        svg.style.border = '1px solid #dee2e6';
        svg.style.borderRadius = '12px';
        svg.style.boxShadow = '0 4px 6px rgba(0, 0, 0, 0.1)';

        // Calculate volume range
        const volumes = data.map(d => parseFloat(d.volume));
        const maxVolume = Math.max(...volumes);
        const scaleY = (volume) => margin.top + chartHeight - ((volume / maxVolume) * chartHeight);
        const barWidth = chartWidth / data.length * 0.8;

        // Draw enhanced volume bars
        data.forEach((item, index) => {
            const x = margin.left + (index / data.length) * chartWidth + (chartWidth / data.length * 0.1);
            const volume = parseFloat(item.volume);
            const y = scaleY(volume);
            const barHeight = chartHeight - (y - margin.top);

            const rect = document.createElementNS('http://www.w3.org/2000/svg', 'rect');
            rect.setAttribute('x', x);
            rect.setAttribute('y', y);
            rect.setAttribute('width', barWidth);
            rect.setAttribute('height', barHeight);
            rect.setAttribute('fill', item.color || (volume > maxVolume * 0.7 ? '#28a745' : '#dc3545'));
            rect.setAttribute('opacity', '0.8');
            rect.setAttribute('rx', '2'); // Rounded corners
            rect.setAttribute('ry', '2');
            
            // Add subtle shadow effect
            rect.style.filter = 'drop-shadow(0 2px 4px rgba(0,0,0,0.1))';
            
            // Enhanced tooltip
            rect.addEventListener('mouseenter', function(e) {
                rect.setAttribute('opacity', '1');
                const volumeFormatted = new Intl.NumberFormat().format(volume);
                showTooltip(e, `Volume: ${volumeFormatted}\nTime: ${item.time}`);
            });
            rect.addEventListener('mouseleave', function() {
                rect.setAttribute('opacity', '0.8');
                hideTooltip();
            });
            
            svg.appendChild(rect);
        });

        // Add title
        const title = document.createElementNS('http://www.w3.org/2000/svg', 'text');
        title.setAttribute('x', width / 2);
        title.setAttribute('y', 15);
        title.setAttribute('text-anchor', 'middle');
        title.setAttribute('font-family', 'Arial, sans-serif');
        title.setAttribute('font-size', '14');
        title.setAttribute('font-weight', 'bold');
        title.setAttribute('fill', '#212529');
        title.textContent = 'Volume Analysis';
        svg.appendChild(title);

        container.appendChild(svg);
        console.log('✅ Volume chart created successfully');

    } catch (error) {
        console.error('❌ Error creating volume chart:', error);
    }
};

// Generate sample candlestick data
function generateSampleCandlestickData() {
    const data = [];
    const now = new Date();
    let price = 111500; // Base price similar to real BTCUSDT

    for (let i = 0; i < 20; i++) {
        const time = new Date(now.getTime() - (19 - i) * 60000); // Every minute
        
        // More realistic price movement - smaller variations
        const trend = Math.sin(i * 0.3) * 10; // Gentle trend
        const noise = (Math.random() - 0.5) * 20; // Small random movements
        const variation = trend + noise;
        
        const open = price;
        const close = price + variation;
        
        // High and low within reasonable bounds
        const volatility = Math.abs(variation) * 0.5;
        const high = Math.max(open, close) + Math.random() * volatility;
        const low = Math.min(open, close) - Math.random() * volatility;

        data.push({
            time: time.toLocaleTimeString().substr(0, 5), // HH:MM format
            open: open.toFixed(2),
            high: high.toFixed(2),
            low: low.toFixed(2),
            close: close.toFixed(2)
        });

        price = close;
    }

    return data;
}

// Generate sample volume data
function generateSampleVolumeData() {
    const data = [];
    const now = new Date();

    for (let i = 0; i < 15; i++) {
        const time = new Date(now.getTime() - (14 - i) * 60000);
        
        // More realistic volume data with better distribution
        const baseVolume = 500000;
        const spike = Math.random() > 0.8 ? Math.random() * 1000000 : 0; // Occasional spikes
        const volume = baseVolume + (Math.random() * 300000) + spike;
        
        const isHighVolume = volume > 800000;
        
        data.push({
            time: time.toLocaleTimeString().substr(0, 5), // HH:MM format
            volume: Math.round(volume).toString(),
            color: isHighVolume ? '#28a745' : (Math.random() > 0.6 ? '#ffc107' : '#dc3545')
        });
    }

    return data;
}

// Tooltip functions
let tooltip = null;

function showTooltip(event, text) {
    hideTooltip();
    
    tooltip = document.createElement('div');
    tooltip.style.position = 'absolute';
    tooltip.style.background = 'rgba(0, 0, 0, 0.8)';
    tooltip.style.color = 'white';
    tooltip.style.padding = '5px 10px';
    tooltip.style.borderRadius = '4px';
    tooltip.style.fontSize = '12px';
    tooltip.style.pointerEvents = 'none';
    tooltip.style.zIndex = '1000';
    tooltip.style.whiteSpace = 'pre-line';
    tooltip.innerHTML = text;
    
    document.body.appendChild(tooltip);
    
    const rect = tooltip.getBoundingClientRect();
    tooltip.style.left = (event.pageX - rect.width / 2) + 'px';
    tooltip.style.top = (event.pageY - rect.height - 10) + 'px';
}

function hideTooltip() {
    if (tooltip) {
        document.body.removeChild(tooltip);
        tooltip = null;
    }
}

console.log('📊 Simple Charts JavaScript loaded successfully');
