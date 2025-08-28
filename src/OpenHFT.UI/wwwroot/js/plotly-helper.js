window.plotlyHelper = {
    isLoaded: function() {
        return typeof Plotly !== 'undefined';
    },
    
    waitForPlotly: function() {
        return new Promise((resolve, reject) => {
            if (typeof Plotly !== 'undefined') {
                resolve();
                return;
            }
            
            let attempts = 0;
            const maxAttempts = 50; // 5 seconds max wait
            
            const checkInterval = setInterval(() => {
                attempts++;
                if (typeof Plotly !== 'undefined') {
                    clearInterval(checkInterval);
                    resolve();
                } else if (attempts >= maxAttempts) {
                    clearInterval(checkInterval);
                    reject(new Error('Plotly failed to load after 5 seconds'));
                }
            }, 100);
        });
    },
    
    newPlot: async function(elementId, data, layout) {
        await this.waitForPlotly();
        return Plotly.newPlot(elementId, data, layout);
    }
};
