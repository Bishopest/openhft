// Advanced Charts for OpenHFT Dashboard

// Wait for Chart.js to load
function waitForChart() {
    return new Promise((resolve, reject) => {
        let attempts = 0;
        const maxAttempts = 50;
        
        function checkChart() {
            if (typeof Chart !== 'undefined') {
                console.log('✅ Chart.js loaded successfully');
                resolve();
            } else if (attempts < maxAttempts) {
                attempts++;
                setTimeout(checkChart, 100);
            } else {
                reject(new Error('Chart.js failed to load after 5 seconds'));
            }
        }
        
        checkChart();
    });
}

// Initialize Candlestick Chart using Chart.js
window.initializeCandlestickChart = async function(chartId, data) {
    try {
        // Wait for Chart.js to be available
        await waitForChart();
        
        const ctx = document.getElementById(chartId);
        if (!ctx) {
            console.error('Chart element not found:', chartId);
            return;
        }

        // Destroy existing chart if it exists
        if (window.candlestickChart) {
            window.candlestickChart.destroy();
        }

        // Ensure we have data
        if (!data || data.length === 0) {
            console.log('No data provided for candlestick chart, generating sample data');
            data = generateSampleCandlestickData();
        }

        // Transform data for Chart.js - simple line chart first
        const chartData = data.map(item => ({
            x: new Date(item.time),
            y: parseFloat(item.close) // Use close price for line chart
        }));

    window.candlestickChart = new Chart(ctx, {
        type: 'line',
        data: {
            datasets: [{
                label: 'Price (USDT)',
                data: chartData,
                borderColor: '#007bff',
                backgroundColor: 'rgba(0, 123, 255, 0.1)',
                fill: true,
                tension: 0.1
            }]
        },
        options: {
            responsive: true,
            maintainAspectRatio: false,
            scales: {
                x: {
                    type: 'time',
                    time: {
                        unit: 'minute',
                        displayFormats: {
                            minute: 'HH:mm'
                        }
                    },
                    title: {
                        display: true,
                        text: 'Time'
                    }
                },
                y: {
                    title: {
                        display: true,
                        text: 'Price (USDT)'
                    }
                }
            },
            plugins: {
                legend: {
                    display: false
                },
                tooltip: {
                    mode: 'index',
                    intersect: false
                }
            }
        }
    });
    
    console.log('✅ Candlestick chart initialized successfully');
    } catch (error) {
        console.error('❌ Error initializing candlestick chart:', error);
    }
};

// Initialize Volume Chart
window.initializeVolumeChart = async function(chartId, data) {
    try {
        // Wait for Chart.js to be available
        await waitForChart();
        
        const ctx = document.getElementById(chartId);
        if (!ctx) {
            console.error('Volume chart element not found:', chartId);
            return;
        }

        // Ensure we have data
        if (!data || data.length === 0) {
            console.log('No data provided for volume chart, generating sample data');
            data = generateSampleVolumeData();
        }

    // Destroy existing chart if it exists
    if (window.volumeChart) {
        window.volumeChart.destroy();
    }

    const labels = data.map(item => item.time);
    const volumes = data.map(item => item.volume);
    const colors = data.map(item => item.color);

    window.volumeChart = new Chart(ctx, {
        type: 'bar',
        data: {
            labels: labels,
            datasets: [{
                label: 'Volume',
                data: volumes,
                backgroundColor: colors,
                borderColor: colors,
                borderWidth: 1
            }]
        },
        options: {
            responsive: true,
            maintainAspectRatio: false,
            scales: {
                x: {
                    title: {
                        display: true,
                        text: 'Time'
                    }
                },
                y: {
                    title: {
                        display: true,
                        text: 'Volume'
                    },
                    beginAtZero: true,
                    ticks: {
                        callback: function(value) {
                            if (value >= 1000000) {
                                return (value / 1000000).toFixed(1) + 'M';
                            } else if (value >= 1000) {
                                return (value / 1000).toFixed(1) + 'K';
                            }
                            return value;
                        }
                    }
                }
            },
            plugins: {
                legend: {
                    display: false
                },
                tooltip: {
                    callbacks: {
                        label: function(context) {
                            const value = context.parsed.y;
                            return `Volume: ${value.toLocaleString()}`;
                        }
                    }
                }
            }
        }
    });
    
    console.log('✅ Volume chart initialized successfully');
    } catch (error) {
        console.error('❌ Error initializing volume chart:', error);
    }
};

// Initialize Portfolio Performance Chart (Updated version)
window.initializePortfolioChart = function(chartData) {
    const ctx = document.getElementById('performance-chart');
    if (!ctx) return;

    // Destroy existing chart if it exists
    if (window.portfolioChart) {
        window.portfolioChart.destroy();
    }

    window.portfolioChart = new Chart(ctx, {
        type: 'line',
        data: {
            labels: chartData[0].x,
            datasets: [{
                label: 'Portfolio Value',
                data: chartData[0].y,
                borderColor: '#007bff',
                backgroundColor: 'rgba(0, 123, 255, 0.1)',
                borderWidth: 3,
                fill: true,
                tension: 0.4,
                pointBackgroundColor: '#007bff',
                pointBorderColor: '#ffffff',
                pointBorderWidth: 2,
                pointRadius: 4,
                pointHoverRadius: 6
            }]
        },
        options: {
            responsive: true,
            maintainAspectRatio: false,
            scales: {
                x: {
                    title: {
                        display: true,
                        text: 'Time'
                    },
                    grid: {
                        color: 'rgba(0,0,0,0.1)'
                    }
                },
                y: {
                    title: {
                        display: true,
                        text: 'Portfolio Value (USDT)'
                    },
                    grid: {
                        color: 'rgba(0,0,0,0.1)'
                    },
                    ticks: {
                        callback: function(value) {
                            return '$' + value.toLocaleString();
                        }
                    }
                }
            },
            plugins: {
                legend: {
                    display: true,
                    position: 'top'
                },
                tooltip: {
                    mode: 'index',
                    intersect: false,
                    callbacks: {
                        label: function(context) {
                            return 'Portfolio Value: $' + context.parsed.y.toLocaleString();
                        }
                    }
                }
            },
            interaction: {
                mode: 'nearest',
                axis: 'x',
                intersect: false
            }
        }
    });
};

// Auto-refresh charts every 5 seconds if data is available
setInterval(function() {
    if (window.candlestickChart || window.volumeChart) {
        // Trigger refresh from Blazor component
        if (window.blazorCulture) {
            DotNet.invokeMethodAsync('OpenHFT.UI', 'RefreshChartsFromJS');
        }
    }
}, 5000);

// Real-time chart updates
window.updateCandlestickChart = function(newData) {
    if (window.candlestickChart && newData) {
        window.candlestickChart.data.datasets[0].data = newData;
        window.candlestickChart.update('none'); // Fast update without animation
    }
};

window.updateVolumeChart = function(newData) {
    if (window.volumeChart && newData) {
        window.volumeChart.data.labels = newData.map(item => item.time);
        window.volumeChart.data.datasets[0].data = newData.map(item => item.volume);
        window.volumeChart.data.datasets[0].backgroundColor = newData.map(item => item.color);
        window.volumeChart.update('none');
    }
};

// Technical Analysis Helpers
window.calculateSMA = function(data, period) {
    const result = [];
    for (let i = period - 1; i < data.length; i++) {
        const sum = data.slice(i - period + 1, i + 1).reduce((a, b) => a + b, 0);
        result.push(sum / period);
    }
    return result;
};

window.calculateEMA = function(data, period) {
    const result = [];
    const multiplier = 2 / (period + 1);
    
    // Start with SMA for first value
    let ema = data.slice(0, period).reduce((a, b) => a + b, 0) / period;
    result.push(ema);
    
    // Calculate EMA for rest of the data
    for (let i = period; i < data.length; i++) {
        ema = (data[i] * multiplier) + (ema * (1 - multiplier));
        result.push(ema);
    }
    
    return result;
};

// Generate sample candlestick data when no real data is available
window.generateSampleCandlestickData = function() {
    const data = [];
    const now = new Date();
    let price = 50000; // Base price
    
    for (let i = 0; i < 20; i++) {
        const time = new Date(now.getTime() - (19 - i) * 60000); // Every minute
        const variation = (Math.random() - 0.5) * 100;
        const open = price;
        const close = price + variation;
        const high = Math.max(open, close) + Math.random() * 50;
        const low = Math.min(open, close) - Math.random() * 50;
        
        data.push({
            time: time.toISOString(),
            open: open,
            high: high,
            low: low,
            close: close
        });
        
        price = close;
    }
    
    return data;
};

// Generate sample volume data when no real data is available
window.generateSampleVolumeData = function() {
    const data = [];
    const now = new Date();
    
    for (let i = 0; i < 20; i++) {
        const time = new Date(now.getTime() - (19 - i) * 60000);
        data.push({
            time: time.toISOString().substr(11, 8), // HH:mm:ss format
            volume: Math.random() * 1000000,
            color: Math.random() > 0.5 ? '#28a745' : '#dc3545'
        });
    }
    
    return data;
};

console.log('📊 Advanced Charts JavaScript loaded successfully');
